---
title: '15-24d — Veterancy: plain units earn rank growth from kills'
type: 'feature'
created: '2026-08-26'
status: 'done'
baseline_commit: '5235a2cca1a4da1fd66de314fb61c8fa0bb6f3e2'
review_loop_iteration: 0
context: ['{project-root}/godot/CLAUDE.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Only heroes progress. Plain units are static from spawn to death, while the sim already
knows who killed whom (`EntityWorld.KillerOf`, written unconditionally at the single
`DamageResolver.KillEntity` choke point) and nothing consumes it for progression. Leg d of story 15-24
makes unit progression creator-authorable and, in doing so, builds the on-kill credit seam that catalog
stats `kill_frenzy` (#19), `cdr_on_kill` (#42) and `kill_bounty` (#45) will later consume.

**Approach:** A creator-opt-in `veterancy` block on `UnitDefinition` declares ascending kill-count ranks,
each carrying a **cumulative** stat-delta set drawn from the closed `StatVocabulary`. One new folded
per-unit counter (`VeterancyKills`) increments at the kill choke point when an opted-in killer lands a
hostile kill; a new `VeterancySystem` re-derives the current rank's vector and installs it on ONE
swap-on-change modifier slot — the `HeroXpSystem.ReconcileThresholds` / `ResearchSystem` cumulative
pattern, verbatim.

## Boundaries & Constraints

**Always:**
- **Opt-in gates the INCREMENT, not just the fold.** A unit whose definition has no `veterancy` block
  never increments, so its counter stays 0 across every recorded scenario. Combined with a bounded fold
  arm (`if (VeterancyKills[i] != 0)`, the v23/v26/v27 house style at `SimChecksum.cs:551-556`), **zero
  goldens move** and the frozen v22 differential control stays byte-identical. This is the epic's
  byte-for-byte default-preservation rule.
- Exactly ONE increment site, beside the existing `KillerOf` write in `DamageResolver.KillEntity`.
- Growth rides the existing modifier-stack channel as a canonical sparse `StatDelta` vector. One stat,
  one channel, many sources — never a per-source stat fork.
- Fixed math only; ascending-entity-id iteration; authored `float` quantizes to `Fixed` at the single
  load/apply boundary. Multi-term sums accumulate in `double` and quantize once per stat.
- The swap must copy the caller-side DW-85 guard (Health snapshot, remove, **post-remove `IsAlive`
  re-check**, rebuild, skip-if-empty, apply, restore + re-clamp). `RemoveByModifierId` is lethal.
- Godot-free. Every acceptance check runs under `dotnet test godot/ProjectChimera.Sim.Tests`.

**Never:**
- No fix to DW-691 here — it starves only the XP `DeathFeed`, not `KillerOf`/`DeathLog`, so ability and
  DoT kills already credit correctly. Leave it open.
- No new drain contract on `DeathFeed` or `DeathLog`; do not change how hero XP is credited.
- No hero behaviour change. No wire or replay change (PROTOCOL and Replay stamps must not move).
- No opening of the stat vocabulary to reflection; no unbounded rank list; no float in sim state.
- Out of scope: a veterancy authoring UI (leg f), rank chevrons or VFX, per-rank abilities, veterancy on
  buildings, and transferring rank on unit conversion.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Opted-in killer, hostile kill | killer alive, def has `veterancy`, victim faction differs | `VeterancyKills[killer] += 1`; crossing a threshold installs that rank's cumulative vector on the single veterancy slot | N/A |
| Non-opted unit kills | killer def has no `veterancy` block | no increment, no modifier, no fold contribution — byte-identical to today | N/A |
| Killer died in the same tick | packed killer ref still resolves to the dead entity | credit is kept, matching `ResolveKillerPayload`'s dead-killer rule | N/A |
| Killer slot recycled | packed ref generation mismatch resolves to −1 | no credit, silently | N/A |
| Attacker-less death | `ModifierStore` external-recompute collapse (Neutral, attacker −1) | no credit | N/A |
| Friendly-fire or self kill | killer faction equals victim faction, or killer is the victim | no increment | N/A |
| Ranks out of order or duplicated | `kills` not strictly ascending, or below 1 | fail-closed: unit rejected | all located errors returned, not first-fail |
| Unknown or non-authorable stat id | `stat_deltas` key outside `StatVocabulary`, or `!ModifierAuthorable` | fail-closed: unit rejected | one located error per bad key |
| Rank vector empty after canonicalization | every delta is 0 | install nothing (DW-678); no empty modifier slot | N/A |
| Save/load mid-rank | unit at rank 2 with 14 kills | counter round-trips; rank re-derives and re-installs on load | fail-closed on lane length mismatch |
| Max rank reached, more kills | kills beyond the last threshold | counter keeps counting; installed vector stays the last rank's | N/A |

</frozen-after-approval>

## Code Map

**Kill credit — the seam already exists, consume it:**
- `godot/src/Combat/DamageResolver.cs:170` — `KillEntity`, the single choke point every lethal path funnels through.
- `godot/src/Combat/DamageResolver.cs:201-202` — `KillerOf` / `KillerFactionOf` written **unconditionally** (packed ref). The increment goes here.
- `godot/src/Core/EntityWorld.cs:564,572` — those arrays. `godot/src/Core/DeathLog.cs:79` — richer `(victim, victimSlot, packedKiller, killerSlot)` record; read-only reference.
- `godot/src/Core/ScenarioDirector.cs:1731` — `ResolveKillerPayload`: the packed-ref deref rule (dead killer keeps credit, recycled slot resolves to −1). Reuse this rule, do not re-invent it.
- `godot/src/Effects/ModifierStore.cs:1033` — the one attacker-less lethal path (Neutral / −1): a defined no-credit case.

**The pattern to mirror — swap-on-change cumulative:**
- `godot/src/Combat/HeroXpSystem.cs:507-549` — `ReconcileThresholds`, **the template**. Note `:528` and `:548-549` (DW-85 snapshot / restore-clamp) and `:534-535` (post-remove `IsAlive`).
- `godot/src/Combat/HeroXpSystem.cs:519-525` — the sparse-vector build idiom: skip `!ModifierAuthorable`, skip `Raw == 0`, then `StatVocabulary.Canonicalize`.
- `godot/src/Economy/ResearchSystem.cs:551` — `ApplyCumulativeModifier`, the original cumulative pattern.
- Modifier ids follow an ASCII-digit story-number scheme (`HeroThresholdModifierId = 0x3135_2400`, `HeroXpSystem.cs:68`). Pick an unused sibling and assert no collision.

**Stat vocabulary:**
- `godot/src/Core/Stats/StatDelta.cs:18`, `StatId.cs:64-101` (17 members), `StatVocabulary.cs:71,149,155,184,250`.

**New folded array (`EntityWorld`) — the full touch-site list:**
- declare near `godot/src/Core/EntityWorld.cs:552-572`; allocate in the ctor block `:1129-1131`; reset per-slot in `Create` `:1288-1295`; clear in `Clear()` `:1915,:1935`.
- **`godot/CLAUDE.md` residue rule:** a per-unit field that is neither def-derived nor a `Create()` ctor arg must be added to all three of `UnitSnapshot`, `SnapshotUnit`, `RestoreUnit`, or it silently reverts on editor delete then undo. That guard is hand-enumerated, not automatic.

**Fold and stamps:**
- `godot/src/Core/SimChecksum.cs:346` — `AlgoVersion = 27` becomes **28** (add a `/// v28 —` history line). Entity loop `:366-368`; copy the **bounded** arm at `:551-556`, never the unconditional one at `:435-437`. SimRng `State` folds LAST.
- `godot/src/Core/Persistence/SaveGameFile.cs:115` — `FormatVersion = 12` becomes **13**.
- `godot/src/Core/Persistence/SaveGameState.cs:191-235` — append the `EA` lane before `PatrolWpX` (`:232`, the flat/stride boundary); allocate near `:373-378`; fill `:405-410`; read `G(EA.X)` `:829-834` and assign `:906-912`.
- The minted veterancy modifier round-trips **free** through DW-997's `KindMintedModifier = 2` (`SaveGameState.cs:186`) provided `periodEffect` is null and it stays a `Modifier`.

**Authoring:**
- `godot/src/Core/Definitions/UnitDefinition.cs:13` — POCO, **lenient-only** (not dual-path): no enums, no `Fixed`, plain `float`, settable auto-props. Mirror the optional `hero` block (`:366-367`, `HeroDefinition.cs:24`) and its nested `Attributes` (`HeroDefinition.cs:92-93`).
- `godot/src/Core/Definitions/FactionWriter.cs:287,297` — `ApplyFields` / `WriteHero`: a form-owned block needs its own omit-null writer arm (`:110`). Never reflection-re-serialize a whole `FactionDefinition` (`:91-96` lists the 8 corruption modes); never re-serialize `Parsed*` (`UnitDefinition.cs:531,540,555,563,578,608`).
- `godot/src/Core/Definitions/ContentHash.cs:68` — `AlgoVersion = 4` becomes **5**. Unit arm `FoldUnitCommon:218-271`; copy the nullable-sub-object arm `FoldHero:277-299` (presence bit `MixInt(0/1)` at `:279`) and the ordinal-sorted dict fold `FoldAttrValues:303-315`.
- `godot/src/Core/Definitions/CanonicalModelHash.cs:203` — `AlgoVersion` **stays 17**. This hash walks the SCENARIO model; `ScenarioData` references units by id string only (`ScenarioData.cs:340`) and never embeds a `UnitDefinition`. The roster fold is `ContentHash.FoldUnitCommon:225`, and `MatchAgreementHash` consumes ContentHash (component 2b) — so ContentHash 4→5 alone makes a pre-15-24d peer unable to agree. Bumping this one moves `StartStateHash` (which mixes it as a content seed) and re-records `hero-start-state.golden.txt` for nothing.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs:57` — returns **ALL** located errors, not first-fail. Hero precedent: `ValidateHero:620` (called `:408`), bounds constants `:62-86`, `CheckHeroGrowth:531`. There is no `Warnings` channel on `UnitValidationResult` — errors only.
- `godot/src/Core/ScenarioApplier.cs:385-387` — the `*_per_level` float-to-`Fixed` quantization boundary. Multi-term sums: `AttributeModelDefinition.cs:251,282-283` (accumulate in `double`, quantize once per stat).

**Guards that go red by design, or catch omissions for free:**
- `Golden/SimChecksumCoverageGuardTest.cs:118` (pins 27) and `:143` (`ExpectedV27Hash` known-state pin — this must NOT move if the arm is genuinely bounded).
- `Meta/VersionStampConsistencyTests.cs:96,223` · `Persistence/SaveLoadTests.cs:458-460` · `Definitions/ContentHashAbsolutePinTests.cs:57` · `Meta/AlgoVersionPinCommentHygieneTests.cs:15`.
- `Definitions/ContentFoldCompletenessTests.cs:26` — red until `"veterancy"` is classified in `UnitFolded` (`:88-100`). **Known gap:** no `_EveryFieldClassified` exists for nested blocks (`:105-159`), so fields *inside* the veterancy type escape the guard — add one.
- `Persistence/EntityWorldSaveCompletenessTests.cs:303-376` — auto-catches a missing save lane. `Sim/EntityWorldClearCompletenessTests.cs` — `Clear()` must reset the array; no exemption is available.
- `Golden/ReBaselineDifferentialGuardTests.cs:70` — the frozen v22 control. **It staying byte-identical is the proof the fold is bounded.** Never re-record it.
- `Bootstrap/PhaseOrderTest.cs` and the `SimulationHost.cs:441` roster summary — update when the system is registered.

## Tasks & Acceptance

**Execution:**
- [x] `godot/src/Core/Definitions/VeterancyDefinition.cs` -- NEW lenient POCO: ordered `ranks[]`, each `{ kills: int, stat_deltas: Dictionary<string,float> }` (cumulative at that rank, not incremental), plus `Clone()`. -- mirrors `HeroDefinition`'s optional-block shape.
- [x] `godot/src/Core/Definitions/UnitDefinition.cs` -- add the nullable `Veterancy` property (`[JsonPropertyName("veterancy")]`). -- the creator opt-in.
- [x] `godot/src/Core/Definitions/FactionWriter.cs` -- add an omit-null `veterancy` writer arm alongside `WriteHero`. -- authored blocks must round-trip; guarded by `FactionWriteRoundTripTests`.
- [x] `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- validate the block: strictly ascending `kills` of at least 1, a bounded rank count, every `stat_deltas` key resolvable in `StatVocabulary` AND `ModifierAuthorable`, each value inside `CheckAuthoringBounds`. Return ALL located errors. -- fail-closed authoring gate.
- [x] `godot/src/Core/Definitions/ContentHash.cs` -- fold the block behind a presence bit with ordinal-sorted deltas, floats as `Fixed.FromFloat(v).Raw`; bump `AlgoVersion` 4 to 5 with a `<para>` note. -- authored content must hash.
- [x] `godot/src/Core/Definitions/CanonicalModelHash.cs` -- **RETRACTED at review (see the Change Log). Leave `AlgoVersion` at 17.** The original task was wrong: this hash does not fold `UnitDefinition`, so the bump was a redundant stamp whose only effect was moving `StartStateHash` and re-recording a golden. `ContentHash` 4→5 is the correct and sufficient cover.
- [x] `godot/src/Core/EntityWorld.cs` -- add `int[] VeterancyKills` across ALL touch sites (declare, allocate, `Create` reset, `Clear`) **and** `UnitSnapshot` / `SnapshotUnit` / `RestoreUnit`. -- the CLAUDE.md residue rule; that guard is hand-enumerated.
- [x] `godot/src/Combat/DamageResolver.cs` -- at the existing `KillerOf` write, increment the killer's counter when the packed ref resolves (live, or dead but not recycled), killer is not the victim, killer faction differs from victim faction, and the killer's `SourceDefinition` opts into veterancy. -- the single increment site; the opt-in gate is what keeps goldens still.
- [x] `godot/src/Combat/VeterancySystem.cs` -- NEW: ascending-id sweep over opted-in alive units; derive the rank from `VeterancyKills` plus the def's thresholds; build the canonical sparse vector; install on ONE swap-on-change slot copying `ReconcileThresholds` verbatim (Health snapshot, `RemoveByModifierId`, post-remove `IsAlive`, skip-if-empty, `Apply`, restore and re-clamp). -- no second folded array: the desired rank is a pure function of the folded counter.
- [x] `godot/src/Core/Sim/SimulationHost.cs` -- register the system adjacent to `HeroXpSystem`; update the roster summary. -- deterministic tick order.
- [x] `godot/src/Core/SimChecksum.cs` -- add the **bounded** fold arm; bump `AlgoVersion` 27 to 28 with a history line. -- fold-on-first-mutability.
- [x] `godot/src/Core/Persistence/SaveGameState.cs` + `SaveGameFile.cs` -- add the `EA` lane (appended before `PatrolWpX`) across allocate, capture, read and assign; bump `FormatVersion` 12 to 13. -- positional lane addressing forces the bump.
- [x] Guard-pin updates: `SimChecksumCoverageGuardTest.cs:118`, `VersionStampConsistencyTests.cs:96,223`, `SaveLoadTests.cs:458-460`, `ContentHashAbsolutePinTests.cs:57`, and `ContentFoldCompletenessTests.cs` (`UnitFolded` plus a NEW nested-block `_EveryFieldClassified` for the veterancy type). -- expected, conscious movement.
- [x] `godot/ProjectChimera.Sim.Tests/Combat/VeterancyTests.cs` -- NEW Tier-1 tests covering **every row** of the I/O matrix, plus: two identical runs are byte-identical; a save and load mid-rank re-derives the same installed vector; a `+max_health` rank does NOT full-heal on rank-up (the DW-85 teeth-test); the modifier round-trips through `KindMintedModifier`. -- prove behaviour and determinism.

**Acceptance Criteria:**
- Given the shipped content set, where no faction authors a `veterancy` block, when the full suite runs, then **all 35 goldens are byte-identical and none is re-recorded**, and the `ReBaselineDifferentialGuardTests` frozen v22 control still hashes `0x6864F671`.
- Given a unit authored with veterancy, when it accumulates kills past each threshold in a headless match, then its effective stats reflect exactly the cumulative vector of the current rank — never the sum of all ranks, and never re-applied on a tick where the rank did not change.
- Given a match with veterancy in play, when the same seed is replayed, then the tick-by-tick `SimChecksum` is identical, and a mid-match save then load resumes a byte-identical checksum stream.
- Given the diff touches none of `src/UI/**`, `src/CreationSuite/**`, `src/Core/Bootstrap/**`, `MainScene.cs` or `scenes/**`, then the in-engine gate does not apply and Tier-1 alone is the gate.

## Spec Change Log

### 2026-08-26 — implemented (as-built record)

**Version stamps moved:** `SimChecksum` 27 → **28** (bounded `VeterancyKills != 0` arm), `ContentHash` 4 → **5**
(the `veterancy` ladder behind a presence bit), `SaveGameFile.FormatVersion` 12 → **13** (the appended
`EA.VeterancyKills` lane). `CanonicalModelHash` **stays 17** — see the retraction below.
`StartStateHash.AlgoVersion`, `MatchAgreementHash`, `RulesetHash` and PROTOCOL/Replay are unmoved.

**Golden movement: NONE.** All 35 goldens are byte-identical and none was re-recorded.
`ReBaselineDifferentialGuardTests`' frozen v22 control still hashes `0x6864F671`, and
`SimChecksumCoverageGuardTest`'s known-state pin still hashes `0x32911831` under v28 — the two standing proofs that
the opt-in gate did not leak.

### 2026-08-26 — review finding: the `CanonicalModelHash` bump was RETRACTED

**Finding.** The spec's task list mandated `CanonicalModelHash` 17 → 18 "for the new authored field". That task was
wrong, and it was the sole cause of the one golden that had moved (`hero-start-state.golden.txt`), two derived value
pins (`ScenarioApplierTests.ExpectedCanonicalHash`, `ProceduralMapGeneratorTests.GoldenHash`) and 16 assertion pins.

**Why it was wrong.** `CanonicalModelHash` folds the SCENARIO model. `ScenarioData` references units by id string
(`ScenarioData.cs:340`) and never embeds a `UnitDefinition`; the roster fold is `ContentHash.FoldUnitCommon:225`.
The implementation's own added doc conceded this ("this hash walks the scenario model, not the faction rosters").
Its stated justification — that a pre-15-24d peer must not agree at the lobby — is already discharged by
`ContentHash` 4 → 5, which IS a `MatchAgreementHash` component (2b) and IS the hash that folds rosters. So the bump
moved every scenario's start-state hash while asserting an algorithm change that did not occur.

**Amendment.** `CanonicalModelHash.cs`, `hero-start-state.golden.txt`, `ScenarioApplierTests.cs` and
`ProceduralMapGeneratorTests.cs` were reverted to the `5235a2cc` baseline; the 16 `Equal(18, …)` pins and
`VersionStampConsistencyTests.ExpectedCanonicalModelHashAlgoVersion` were returned to 17. AC1 ("all 35 goldens
byte-identical") now holds literally, as designed.

**KEEP on re-derivation:** an `AlgoVersion` moves when THAT hash's fold set or algorithm changes — never as a
generic "content changed" signal. Route a content-model change to the hash that actually folds it. Bumping a
neighbouring stamp is not free: it re-records goldens and destroys the zero-movement property that is a bounded
fold's only proof it stayed bounded.

**Deviations from the task list, both deliberate:**
1. `VeterancySystem.QuantizeDeltas` walks the `StatVocabulary` REGISTRY in ascending `StatId` and looks each
   `JsonName` up in the authored map, instead of enumerating the map and accumulating in `double`. Each `StatId`
   owns exactly one `JsonName`, so a rank carries at most ONE term per stat and there is no multi-term sum to
   widen — the rule's operative half ("quantize once per stat") is met by there being a single `Fixed.FromFloat`
   per stat, and the registry walk additionally makes the result independent of authored key order (S-CORE-1 /
   CHM0002), which a dictionary enumeration would not have been.
2. The editor `Duplicate` path (`CloneUnit`/`CloneBuilding`) was NOT taught to copy the block, because both live in
   `src/CreationSuite/**` and AC4 pins the diff out of the in-engine gate. `VeterancyDefinition.Clone()` exists and
   is deep-copy tested for leg f to use; the gap is filed as **DW-999** together with the missing form affordance
   and the missing rank cue.

**Extra guard added beyond the list:** `ContentFoldCompletenessTests` gained the NESTED-block
`_EveryFieldClassified` the spec's Code Map flagged as a known gap, for both `VeterancyDefinition` and
`VeterancyRank`, plus their fold-actuality sweeps — so a future field added INSIDE a rank cannot deserialize and
escape the handshake.

## Review Triage Log

### 2026-08-26 — review pass 1 (blind-hunter · edge-case-hunter · verification-gap)

Three layers ran blind against the diff. **No `intent_gap` and no `bad_spec`** — the one spec-level defect
(the `CanonicalModelHash` task) was caught and amended during step 3, before this pass. 11 findings routed to
`patch`, 10 to `defer` (DW-1000 … DW-1009). Dismissals below, each with the reason that disposed of its claim.

**APPLIED — the 11 patch findings (2026-08-26).**

| # | Finding | Fix |
|---|---|---|
| P1 | `VeterancySystem.Tick`'s two early-outs `continue`d without removing an installed vector, so an ORPHANED rank kept applying stats forever (a permanent `StackRule.Ignore` descriptor expires on nothing). Reachable two ways across a save/load, because the minted descriptor round-trips BY VALUE while the rank's two inputs restore independently: the v13 lane floors a tampered negative counter to 0, and the `veterancy` block can be edited out of the def between save and load. | New `StripOrphan`, called from BOTH exits, carrying the same DW-85 snapshot / post-remove `IsAlive` discipline as the swap. Returns on one `_count` read when nothing is installed — the hot path every golden is made of. The factually-wrong `— inert` comment is gone. 4 tests. |
| P2 | `ModifierStore.Apply`'s `bool` was discarded, so a ring-full host silently never got its rank while Health was re-stated as though it had. | Return honoured: `ringMoved` gates the Health re-statement, and a refusal increments the new **`RefusedRankInstalls`** diagnostic (unfolded, unpersisted — the `ModifierStore.RefusedInstallCount` / `SkippedPulseCount` posture). The tally exists because the probe proved the control-flow change alone was UNOBSERVABLE: the skipped Health write rewrites the value it just read, so a test could not tell the fix from its absence. The retry on the next tick is kept and pinned — a rank must land the moment a slot frees. 3 tests. |
| P3 | `CheckVeterancyDeltas` delegated the `ModifierAuthorable` gate to `Modifier.CheckAuthoringBounds`, whose walk visits ZERO entries when the delta quantizes to 0 — so `"max_energy": 0` validated clean, contradicting the matrix row. | The gate is checked directly on the registry row, before the bounds probe, with `Modifier.CheckDelta`'s own message. 2 tests (the reject and its complement, an authorable 0, which stays valid). |
| P4 | `BuildingDefinition : UnitDefinition` and `ContentHash.FoldBuilding` calls `FoldUnitCommon`, so a building ladder validated, folded and SAVED — and could never run (`VeterancySystem` sweeps `EntityWorld`; buildings live in `BuildingStore`). The Never list scopes veterancy off buildings. | Fail-closed located error in the validator, typed on `BuildingDefinition` rather than on the `kind` string so a caller that forgets `kind:"building"` is still gated. The ContentHash fold stays symmetric (the completeness guard requires it); the validator is the gate. 1 test. |
| P5 | The v13 negative-count restore floor had NO test — delete it and nothing went red. | Two pins in `EffectiveStatRestoreClampTests` beside its DW-692 siblings: tamper negative → restores 0, and a well-formed count restores bit-exact (the fence proving the floor is a no-op on every save the game can write). |
| P6 | `QuantizeDeltas`' two runtime fail-safes were exercised only through the validator — and NO LOADER calls `UnitDefinitionValidator` (only the CreationSuite panels and the LLM gate do), so raw-JSON faction content reaches the sim unvalidated and those `continue`s are live policy. | Three runtime tests that tick the system with an unvalidated ladder: a non-authorable key, a non-finite value, and an all-unknown rank. Each guard probed separately. |
| P7 | The class doc claimed the boundary accumulates "multi-term sums in `double`". No such accumulation exists. | Doc corrected to the actual registry-walk rationale (one `JsonName` per `StatId` ⇒ at most one term per stat ⇒ nothing to widen; the walk is over the registry so authored key order cannot matter). |
| P8 | The insertion at [13] left `SimulationHost` prose stale (`ItemSystem [13]`, `ScenarioDirector [18]`, "before the director, index 18"). | The two indices I shifted are corrected, `VeterancySystem` is named as a third DW-325 ceiling-collapse producer (which the new `SystemOrderTest` assertion now pins), and five OTHER present-tense index claims — stale before this story — were rewritten as un-rottable position statements ("registered LAST", "immediately after ProjectileSystem") rather than re-pinned to numbers that will rot again. |
| P9 | `NonOptedKiller_NeverIncrements_AndFoldsNothing`'s comment described a second world that kills; it kills nothing. | Comment rewritten to describe the asymmetry that IS asserted. No assertion changed. |
| P10 | DW-999's `CloneBuilding` half was incoherent (P4 makes a building ladder illegal, so the prescribed line would copy state that must not exist). | That half dropped; the entry now names `CloneUnit` only, records the GENERAL DW-72 hand-enumerated-clone risk as the real structural fix, and adds the missed presentation half — a ranked unit renders an UNLABELED permanent icon in the Story 11.5 buff row (`SelectionSubgroupPanel.cs:293`), not merely a missing chevron. |
| P11 | The `sprint-status.yaml` block was written pre-retraction and claimed CanonicalModelHash 17→18, "all 34 goldens" and a hero-start-state re-record. | Rewritten as TEXT to the truth: CanonicalModelHash stays 17, all 35 goldens byte-identical, zero re-records, plus the review summary. |

**DISMISSED — `NonOptedKiller_NeverIncrements_AndFoldsNothing` "only proves two identical constructions hash
alike" (blind-hunter).** Refuted at the site. The two worlds are NOT identical constructions: world 1 runs five
spawn-and-kill cycles before its comparison, world 2 runs none. The assertion — that a world which has spawned
and destroyed five entities hashes identically to a fresh one — is meaningful and is a real residue check. Only
the test's COMMENT is wrong (it claims world 2 also kills). Kept as a comment-only patch (P9); the finding's own
claim about the assertion's worth does not survive.

**DISMISSED — "the spec's own metadata contradicts its body" (blind-hunter):** `status: 'in-progress'` and an
empty `## Review Triage Log` while the body records an as-built result. Disposed of by rule: a finding whose fix
edits the spec this build is implementing is dismissed. Also self-resolving — status advanced to `in-review` when
this pass began, and this entry fills the section.

**DISMISSED — `Snapshot.md` is stale in both directions (blind-hunter).** The claim is TRUE (the stamps block
still reads SimChecksum 27 / ContentHash 4 / Save 12, and the open-threads list still names leg d as residual),
but `Snapshot.md` is an agent-context tracker, and the rule is defer-never-patch for those. It is additionally
governed by the project's own session protocol, which adds a new dated Current State block at `/save` rather than
editing in place. Handled at story wrap-up under that protocol, not as a review patch.

**KEPT but routed to `defer`, not `patch`** — allied same-team kills, building kills, revive-resets-rank,
per-tick allocation, neutral-creep farming, the GDD/sample-content gap, the private rank bound, the
`FactionWriter` unmodelled-key drop, the inert mistyped-key rank, and the missing `CloneUnit` red test. Each is a
real consequence verified at its site, but each is either a design question the spec deliberately left open, a
pre-existing class this story only exposed, or a fix landing in `src/CreationSuite/**` which AC4 pins outside this
story's diff. Filed as DW-1000 … DW-1009 rather than guessed at here.

## Design Notes

**Why opt-in gates the increment, not just the fold.** A bounded fold arm alone would not be free: the
recorded golden scenarios contain combat, so an ungated counter would go non-zero mid-replay and move
every golden. Gating the *increment* on the authored block means shipped content never leaves identity,
so the bounded arm folds zero `Mix` calls. The frozen v22 differential control staying byte-identical is
the test that proves it — if that control moves, the gate leaked.

**Why no second folded array.** `HeroXpSystem` folds `GrowthStacksApplied` because its growth is
*additive* (`desired − applied` stacks). Veterancy is *cumulative per rank* — the installed vector is a
pure function of `VeterancyKills` and the definition — so the swap can compare desired against installed
and needs no applied-rank memory. That also makes load self-healing: re-derive on restore, no extra lane.

**Why credit at the choke point rather than draining `DeathLog`.** Every lethal path already funnels
through `KillEntity`, and `KillerOf` is written there unconditionally — including the effect-graph and
DoT paths that DW-691 starves of `DeathFeed` records. Crediting there means ability kills count without
touching DW-691, and adds no second consumer contract to a transient per-tick log.

## Verification

**Result (2026-08-26): PASS.**
- `dotnet build godot/godot.csproj` — 0 errors, 15 warnings, all pre-existing and all in files this story did not
  touch (`MainScene.cs`, `DedicatedServer.cs`, `UnitCardPanel.Edit.cs`, `MainMenuPhase.cs`, `ScoreScreenOverlay.cs`).
- `dotnet build godot/ProjectChimera.Sim.Analysis --no-incremental` — **RS0030 = 0** (the zero-baseline gate holds).
  The two new sim files contribute 4 ADVISORY diagnostics of the pre-existing authored-content class: CHM0001 on the
  lenient POCO's `float` fields (exactly what `HeroDefinition` carries) and one CHM0005 on the single
  `Fixed.FromFloat` quantize boundary in `VeterancySystem.QuantizeDeltas`.
- `dotnet test godot/ProjectChimera.Sim.Tests` — **7061 passed / 0 failed / 1 skipped** (7062 total), including 41
  `VeterancyTests` and the two new `EffectiveStatRestoreClampTests` veterancy-lane pins.
  `EntityWorldSaveCompletenessTests`, `EntityWorldClearCompletenessTests`, `ContentFoldCompletenessTests`,
  `FactionWriteRoundTripTests` and `ReBaselineDifferentialGuardTests` all green with no allowlist edits.
  `git diff --name-only <baseline> -- '*.golden.txt'` is EMPTY — **zero goldens moved, zero re-records.**
  One tolerated flake on the first full run (`LlmSupersededDraftCallbackTests.ADraftCancelledAfterTheProviderReturned_NeverEnqueuesItsCallback`,
  an async-timing test untouched by this story): passed in isolation and on the immediate full re-run — the
  documented `CanonicalModelHashPerf` CPU-contention class.
- **Teeth verified, not assumed.** Every guard this pass added was probed by temporarily reverting the fix it
  guards and confirming it went RED: the two orphan-strip exits, the `Apply` refusal tally, the direct
  `ModifierAuthorable` gate, the building reject, the v13 negative-count restore floor, and BOTH `QuantizeDeltas`
  runtime drops (`ModifierAuthorable` and `IsFinite`, probed separately). An earlier version of the ring-full test
  was found VACUOUS this way — honouring `Apply`'s bool had no observable effect — which is what motivated the
  `RefusedRankInstalls` tally rather than a silent control-flow change.
- In-engine gate: **does not apply.** The diff touches no `src/UI/**`, `src/CreationSuite/**`,
  `src/Core/Bootstrap/**`, `MainScene.cs`, `scenes/**` or any `.tscn`/`.tres`.


**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors; banned-API / RS0030 analyzer clean (no float, no `System.Random`, no Godot types in the new sim files).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: green; the new `VeterancyTests` pass; every golden test passes **without** re-recording; `EntityWorldSaveCompletenessTests`, `EntityWorldClearCompletenessTests`, `ContentFoldCompletenessTests`, `FactionWriteRoundTripTests` and `ReBaselineDifferentialGuardTests` all green. The one tolerated failure is the documented `CanonicalModelHashPerf` CPU-contention flake — re-run it in isolation to confirm.
- Golden re-record is **not expected**. If a golden does move, stop and diagnose the opt-in gate before re-recording anything.

## Suggested Review Order

**Kill credit — the one increment site**

- Start here: four gates in order, and the opt-in gate is why no golden moved.
  [`DamageResolver.cs:208`](../../godot/src/Combat/DamageResolver.cs#L208)

- The single new piece of folded state; everything else re-derives from it.
  [`EntityWorld.cs:605`](../../godot/src/Core/EntityWorld.cs#L605)

**Rank application — swap-on-change, no applied-rank memory**

- The sweep and its two early-outs, each now stripping an orphaned slot.
  [`VeterancySystem.cs:84`](../../godot/src/Combat/VeterancySystem.cs#L84)

- Review-found: a counter floored to 0 must not leave a rank installed.
  [`VeterancySystem.cs:122`](../../godot/src/Combat/VeterancySystem.cs#L122)

- The heart: desired-vs-installed compare, DW-85 snapshot, lethal-remove re-check.
  [`VeterancySystem.cs:166`](../../godot/src/Combat/VeterancySystem.cs#L166)

- Registry walk, not map enumeration — authored key order cannot change the result.
  [`VeterancySystem.cs:258`](../../godot/src/Combat/VeterancySystem.cs#L258)

**Determinism — fold, persistence, editor residue**

- Bounded arm: an entity at 0 folds zero Mix calls. The golden-neutrality proof.
  [`SimChecksum.cs:580`](../../godot/src/Core/SimChecksum.cs#L580)

- Appended lane forcing FormatVersion 13; note the negative-count restore floor.
  [`SaveGameState.cs:239`](../../godot/src/Core/Persistence/SaveGameState.cs#L239)

- Caller-owned residue: without this, editor delete-then-undo silently wipes a rank.
  [`UnitSnapshot.cs:62`](../../godot/src/Core/UnitSnapshot.cs#L62)

**Authoring — schema, fail-closed validation, content hash**

- The opt-in block: ascending ranks, cumulative-per-rank stat deltas.
  [`VeterancyDefinition.cs:64`](../../godot/src/Core/Definitions/VeterancyDefinition.cs#L64)

- Mirrors the optional `hero` block exactly; lenient-loader contract applies.
  [`UnitDefinition.cs:375`](../../godot/src/Core/Definitions/UnitDefinition.cs#L375)

- Ladder shape rules, plus the review-found building rejection.
  [`UnitDefinitionValidator.cs:720`](../../godot/src/Core/Definitions/UnitDefinitionValidator.cs#L720)

- Review-found: authorable gate checked directly, so a zero value cannot slip through.
  [`UnitDefinitionValidator.cs:799`](../../godot/src/Core/Definitions/UnitDefinitionValidator.cs#L799)

- Presence bit then ordinal-sorted deltas — the ContentHash 4 to 5 mover.
  [`ContentHash.cs:289`](../../godot/src/Core/Definitions/ContentHash.cs#L289)

- Omit-null writer arm so an unauthored ladder round-trips as absent.
  [`FactionWriter.cs:288`](../../godot/src/Core/Definitions/FactionWriter.cs#L288)

**Wiring and tests**

- Registered at [13] beside HeroXpSystem; tick order is pinned by test.
  [`SimulationHost.cs:399`](../../godot/src/Core/Sim/SimulationHost.cs#L399)

- Every I/O matrix row, plus the anti-vacuity guard on the determinism proof.
  [`VeterancyTests.cs:1`](../../godot/ProjectChimera.Sim.Tests/Combat/VeterancyTests.cs#L1)
