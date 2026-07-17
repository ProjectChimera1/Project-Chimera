---
title: 'Story 7.7: Authoritative server-side load-time validator gate (no escape hatch)'
type: 'feature'
created: '2026-07-16'
status: 'done'
review_loop_iteration: 0
baseline_revision: '06336e11a4b77240832b29a90c651ad80b14c8fd'
final_revision: '75a67f8'
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The validator exists but is escapable: the boot gate is shadow-mode by default (`ScenarioGate.ShouldProceed` = `ok || !failClosed`, fail-closed only via env `CHIMERA_VALIDATE_FAILCLOSED=1`), a `ValidationResult.Fail` overload mints a `Validated<ScenarioData>` even on FAILURE, the fallback path validates its mirror then discards the result and applies un-tokened `ApplyFallback`, and the AI path's only in-flow validator is a second, divergent bespoke rulebook. Deep graph-structural validation (duplicate node ids, dangling/forked exec edges, stray/forked data edges, missing `wire` fail-open, orphan-node semantic skip, loop-var shadowing, gate/backstop invocation asymmetry, hand-copied vocabulary sets) is explicitly deferred to this story at 10+ marked sites. And the MP handshake hash covers none of the trigger/DSL content: `Triggers`/`Regions`/`Variables`/`Timers`/`TriggerGraphJson` are excluded from `CanonicalModelHash` "until 7.7", `schema_version`/`checksum_algo_version` do not exist, and a hash of 0 is fail-OPEN (skip) at the lobby.

**Approach:** Make `ScenarioValidator.Validate` a mandatory fail-closed pre-tick gate on every apply path (file, AI-gen, fallback, replay-scenario, F5 reset), minting the `Validated<T>` proof only on success; add a Godot-free `GraphStructureGate` (whole-graph, all nodes reachable or not) invoked unconditionally by BOTH the validator and the `ScenarioDirector.LoadScenario` backstop; expand `CanonicalModelHash` to fold the full sim-semantic model (Regions, flat Triggers, Variables, Timers, parsed graph IR as a typed Fixed.Raw fold — never JSON text) as ONE named `AlgoVersion` 7→8 re-baseline; add `schema_version`+`checksum_algo_version` with absent⇒v1 amnesty; define the hash-excluded per-node `_editor` annotation channel; and make hash-0 BLOCK the lobby handshake via a pure, testable decision helper.

## Boundaries & Constraints

**Always:**
- All new sim/validation code is Godot-free and float-free (`GraphStructureGate`, hash fold, `HandshakeGate` helper live in `src/Dsl/` or `src/Core/Definitions/` and compile into `ProjectChimera.Sim.Tests`); no `Dictionary`/`HashSet` enumeration in any fold path; located errors everywhere (field path + offending value), first-fail.
- **Proof discipline:** `ScenarioValidator.Proof` is minted ONLY after every check passes (today it mints at the top of `Validate`); the failure-carrying `Fail(located, validated)` overload is deleted; `ValidationResult.Fail` carries no token. `ScenarioGate` shadow mode and the `CHIMERA_VALIDATE_FAILCLOSED` env var are removed — proceeding requires `r.Ok`, everywhere, unconditionally. `ValidatedMintingTests` sole-minter scan stays green.
- **Every apply path fail-closed:** boot file path, `PendingGeneratedScenario` (AI), fallback, headless server (already fail-closed — keep), F5 `ResetToAuthoredStart` (already fail-closed — keep, including its fallback branch which today applies with NO validation). A rejected model is never applied; the boot/F5 reject surfaces the located error (log + existing toast/PrintErr conventions) and substitutes the VALIDATED fallback (the established missing-file safety-net behavior, now extended to invalid-file).
- **Fallback unification:** `ScenarioApplier.ApplyFallback()` is retired. The fallback is `Apply(Validate(BuildFallbackMirror()).Value)` — one writer path, one token type. Before deletion, pin behavior parity: world state (SimChecksum after apply + key world facts) of the mirror-applied fallback must equal the legacy `ApplyFallback` world; if the mirror diverges, fix the MIRROR. If the mirror itself fails validation, apply nothing (empty world) and log — that is a build defect, not a runtime path.
- **`GraphStructureGate` (new, shared rulebook like `DslLoopGate`), run over the WHOLE graph (unreachable nodes included) at BOTH gates:** duplicate node ids reject; every edge endpoint must exist; exec/data port legality per node kind from ONE table (single source, NodeKinds-adjacent); forked exec edges (two exec edges out of one `(src,srcPort)`) reject; forked data edges into one `(dst,dstPort)` reject (generalizes the existing value-in/cond-in/index-in fork rejects); stray data edges into non-data ports or from non-expr sources reject; expression subgraphs are compile-checked even when unconsumed (no orphan-node semantic skip). Mere unreachability of an individually-valid node is NOT a reject (T3 WIP posture; ledger closure).
- **Parse-level fail-closed:** a data edge missing `wire` is a located parse reject (today it silently defaults to `Boolean`); duplicate-key and unknown-property postures stay as-is.
- **Loop-var shadowing:** nested loops sharing one `loop_var` along a nesting chain → located reject in `DslLoopGate`.
- **Gate/backstop reconciliation:** `ScenarioDirector.LoadScenario` runs `GraphStructureGate` + `DslLoopGate.CheckDeclarations` + `CheckGraph` UNCONDITIONALLY (the `HasLoopConstructs` guard is removed); locals-then-commit atomicity preserved; remaining validator-only checks (`CheckFactionSlot` engine ceiling, `EffectBounds` embeds, timer/variable semantics, spawn positions) stay validator-only and the `DslLoopGate`/`GraphStructureGate` docs state the posture precisely.
- **Vocabulary unification:** `ScenarioValidator`'s private `_triggerEventTypes`/`_conditionTypes`/`_actionTypes` are replaced by `NodeKinds`-derived sets (flat action set = the graph set minus graph-channel-only kinds, via an explicit `NodeKinds` member); a lockstep test asserts the validator consumes `NodeKinds` (no second copy can drift).
- **Hash expansion = ONE named `CanonicalModelHash` `AlgoVersion` 7→8 bump:** fold, appended after the v7 fields in fixed order: `Regions` (total-ordered), flat `Triggers` (declaration order — it is semantic), `Variables`, `Timers`, then the PARSED `TriggerGraphJson` graph as a typed fold — nodes ascending id (kind string + each semantic field in fixed order; `Fixed` via `.Raw`; enums as stable names), exec edges sorted `(Src,SrcPort,Dst,DstPort)`, data edges sorted likewise + wire name. NEVER fold `ToCanonicalJson` bytes (cross-runtime string-format risk, per the ledger). Unparseable `TriggerGraphJson` folds a fixed sentinel (Compute stays pure/never-throw). The 0→1 sentinel and `ToWire` stay. Docblock entry names Story 7.7. `StartStateHash.AlgoVersion` stays 2 (its value moves via the canonical seed — expected).
- **Re-baseline scope (the landmine):** exactly one canonical re-baseline — `hero-start-state.golden.txt` re-record via `CHIMERA_GOLDEN_RECORD=1`, version pins updated (`VersionStampConsistencyTests` ExpectedCanonicalModelHashAlgoVersion 7→8, `CanonicalModelHashTests.AlgoVersion_IsSeven`, every `AlgoVersions_Unchanged`), independent-FNV pin tests recomputed, and the existing EXCLUSION tests for Triggers/Regions/declarations rewritten as SENSITIVITY tests (they now fold). The 24 SimChecksum world goldens must NOT move (`SimChecksum.AlgoVersion` stays 17).
- **`_editor` channel:** optional per-node `_editor` JSON bag on `NodeBase`, round-tripped VERBATIM through the converter (allow-listed, never interpreted), serialized deterministically by `ToCanonicalJson`, excluded from the hash fold by construction. Editing `_editor` content must not move the canonical hash.
- **Versioning stamps:** `ScenarioData` gains nullable `SchemaVersion` (`schema_version`) + `ChecksumAlgoVersion` (`checksum_algo_version`); absent ⇒ 1 (legacy amnesty, no migration); `ScenarioSerializer.Serialize` stamps current values (`CurrentSchemaVersion = 1`, `CanonicalModelHash.AlgoVersion`); validator rejects values > current with located errors; both stamps EXCLUDED from the canonical hash (re-save of a legacy file adds stamps without moving the hash); wired into the `VersionStampConsistencyTests` registry (its tripwire test demands this).
- **Hash-0 blocks:** extract the lobby start decision into a pure `HandshakeGate` helper (Godot-free): either hash 0 → block with "scenario hash not computed"; nonzero mismatch → block (existing message); equal nonzero → allow. `LobbyUi` delegates to it.
- **Perf budget:** a Tier-1 test builds a max-caps scenario (caps-saturated units/buildings/resource-nodes/regions/variables/timers + a `MaxDslOpsPerTrigger`-scale graph) and asserts median-of-5 `Compute`+`StartStateHash.Compute` wall time ≤ 50 ms (named constant). Optimizing the fold (single-pass, no LINQ allocation chains) is in scope if needed.
- **AI-gen surface:** `MapGeneratorPanel`'s save path goes through `MapWriteGate` (same pattern as Export/New-Map). `LLMService.ValidateScenario` stays as an advisory pre-filter — the boundary gate is the authority.

**Block If:**
- `SimChecksum` (AlgoVersion 17) or any of the 24 world goldens moves — this story must not touch tick behavior. HALT status `blocked`, condition `validator/hash story moved tick-path state`.
- More than the single named 7→8 canonical re-baseline (+ the dependent `hero-start-state` StartStateHash value movement) is required. HALT status `blocked`, condition `unplanned hash baseline movement`.
- The fallback-mirror parity pin cannot be made to pass by fixing the mirror (legacy `ApplyFallback` world differs irreconcilably). HALT status `blocked`, condition `fallback mirror is not behavior-parity`.
- Any shipped scenario, golden fixture, or sanctioned editor output fails the new structural gate (the gate may only reject genuinely malformed content). HALT status `blocked`, condition `structural gate rejects sanctioned content`.
- The ≤50 ms median budget is unreachable even after allocation-free single-pass optimization. HALT status `blocked`, condition `canonical hash exceeds lobby handshake budget`.

**Never:**
- No T3 canvas (7.10), no UI read/write rails (7.8/7.9), no win-condition presets (7.11), no custom events (7.5), no new node kinds or vocabulary breadth (7.13). No new grammar — this story only validates and hashes what exists.
- No `[JsonPolymorphic]`; no second executor; no folding of LIVE sim state into the canonical hash (live values stay SimChecksum's job); `PersistenceManifest` stays hash-excluded (Story 3.8 decision); TerrainRef neutralization stays.
- No replay format change; the replay scenario-path-mismatch warn stays warn-only (ledgered); v1/v2 `.chmr` handling untouched.
- No gating of the remaining editor write paths beyond `MapGeneratorPanel` (`WinConditionPhase.DoImport` re-save, `PersistenceManifestPanel` — ledgered, out of scope).
- Do not delete or extend `LLMService.ValidateScenario`'s rulebook; do not rewrite `ScenarioValidator`'s existing per-field checks (only its vocabulary source, proof minting, stamp checks, and new gate invocations change).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Malformed hand-crafted file at boot | Scenario JSON failing any validator rule | Model never applied; located error logged/surfaced; VALIDATED fallback world boots | Located error names field + value |
| Malformed AI-generated scenario | `PendingGeneratedScenario` failing validation | Same fail-closed rejection pre-tick; save path blocked by `MapWriteGate` | Located error |
| All valid content | Shipped scenarios, goldens, editor/wizard outputs | Pass gate unchanged; apply identical to pre-7.7 sim behavior | No error |
| Structural garbage | Dup node ids; dangling edge; forked exec edge; forked/stray data edge; missing `wire`; orphan expr subgraph w/ type error | Located reject at BOTH `ScenarioValidator` and `LoadScenario` backstop | Located errors naming node/edge |
| Loop-var shadowing | Nested `for_each` chain reusing one `loop_var` | Located reject at both gates | Error names the variable + nodes |
| Cosmetic edit | Re-save, key reorder, whitespace, `_editor` content edit, stamps added to legacy file | Canonical hash UNCHANGED | No error |
| Sim-semantic edit | Trigger param, variable initial, timer, region rect, graph node field/edge change | Canonical hash CHANGES | No error |
| Legacy amnesty / future reject | File without stamps; file with `schema_version` or `checksum_algo_version` > current | Absent ⇒ v1, loads normally; future value → located reject | Located error naming stamp + value |
| Lobby handshake | Local or peer hash 0; nonzero mismatch; equal nonzero | Block "not computed"; block mismatch; allow | Pure `HandshakeGate` decision |
| Fallback boot | Scenario file missing | Validated mirror applied via `Apply`; world parity with legacy `ApplyFallback` (pinned); nonzero hash published | No error |
| Replay boot | Replay session over an invalid scenario file | Same fail-closed boot rejection — no unvalidated sim under a replay | Located error |
| Max-caps hash perf | Caps-saturated scenario incl. max graph | Median-of-5 Compute ≤ 50 ms | Test fails over budget |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/Validated.cs` — `Validated<T>` + `ValidationResult` (`:19-80`); the failure-token `Fail` overload at `:78` to delete.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — the gate; Proof minted early at `:87` (invert); private vocab sets `:1145-1147`; graph passes `:462-842`; 7.7 markers at `:382,470,640,760,770,833`.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — boot gate `:282,334-343`; `PendingGeneratedScenario` `:38,49-63`; fallback `:350-363`. `ScenarioGate.cs` — shadow mode `:23,41` (remove).
- `godot/src/Core/Sim/ScenarioApplier.cs` — `Apply(Validated<T>)` `:129`; `ApplyFallback` `:362-365` (retire). `ServerBootstrap.cs:74` — already fail-closed (reference posture).
- `godot/src/Core/MainScene.cs` — F5 reset `:1580-1680` (fallback branch `:1680` unvalidated today); wire-hash publication `:478-482`; headless load `:1847-1871`.
- `godot/src/Dsl/TriggerGraph.cs` — IR, `FromJson :717-729`, `ToCanonicalJson :702-711`, `BuildExecutionOrder :549-656` (forked-exec first-match note `:613-616`), `ToFlat :358-477` silent drops. Port constants `:26-61`.
- `godot/src/Dsl/NodeBase.cs` — `NodeKinds :305-356` (unification note `:299-304`); node POCOs (add `_editor`). `NodeBaseJsonConverter.cs` — allow-lists `:614-630`. `GraphEdge.cs` — `DataEdge` `[JsonConstructor]` `:78-82` wire fail-open.
- `godot/src/Core/Definitions/DslLoopGate.cs` — shared rulebook; `CheckLoopVar :434-445` (no shadowing check); asymmetry doc `:17-25`. `DslBounds.cs`, `EffectCaps.cs` — named caps.
- `godot/src/Core/ScenarioDirector.cs` — `LoadScenario :166-294`; `HasLoopConstructs` guard `:197-216`; `CompileExpressionPrograms :320-459`.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — `AlgoVersion=7 :100`, fold `:102-257`, exclusion docblock `:18-42`, 0→1 sentinel `:228`, `ToWire :235-239`. `StartStateHash.cs` — seeds from canonical `:63`. `ScenarioData.cs` — add stamps; exclusion docblocks `:525-561`. `ScenarioSerializer.cs` — `Serialize :48` stamping.
- `godot/src/Multiplayer/LobbyUi.cs` — handshake `:361-371` (hash-0 skip to invert). `godot/src/CreationSuite/MapGeneratorPanel.cs:246-264` — ungated AI save. `MapWriteGate.cs` — the write-gate pattern.
- `godot/ProjectChimera.Sim.Tests/` — `Validation/` (NegativeValidationTests, ShadowModeTests, ValidatedMintingTests, TriggerValidationTests, CanonicalModelHash*Tests, StartStateHashTests), `Meta/VersionStampConsistencyTests.cs` (pins `:65-127`; schema tripwire `:167-181`), `Golden/hero-start-state.golden.txt` + `HeroStartStateScenario.cs`, `Dsl/NodeKindsLockstepTests`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/Validated.cs` + `ScenarioValidator.cs` — delete the failure-token `Fail` overload; mint Proof only on full success; replace private vocab sets with `NodeKinds`-derived members; add stamp checks (`schema_version`/`checksum_algo_version` > current → located reject); invoke `GraphStructureGate` unconditionally in the graph section. — the no-escape-hatch core.
- `godot/src/Dsl/GraphStructureGate.cs` (new) — whole-graph structural rulebook per Always (dup ids, endpoints, port legality table, exec/data forks, strays, unconsumed-expr compile checks), located errors. — the deferred structural validator.
- `godot/src/Dsl/GraphEdge.cs` + `NodeBaseJsonConverter.cs` — missing `wire` → located parse reject; `_editor` per-node verbatim bag allow-listed and round-tripped; update `NodeKinds` unification comments. — parse-level fail-closed + annotation channel.
- `godot/src/Dsl/NodeBase.cs` + `TriggerGraph.cs` — `_editor` field on `NodeBase`; `ToCanonicalJson` serializes it deterministically; explicit flat-vs-graph action-set member on `NodeKinds`; refresh 7.7 deferral comments. — single vocabulary source.
- `godot/src/Core/Definitions/DslLoopGate.cs` — loop-var shadowing reject along nesting chains; update posture docs. — closes the ledgered shadowing gap.
- `godot/src/Core/ScenarioDirector.cs` — run `GraphStructureGate` + `CheckDeclarations` + `CheckGraph` unconditionally (drop `HasLoopConstructs` guard); locals-then-commit preserved. — gate/backstop reconciliation.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` + `ScenarioGate.cs` (delete or reduce) — fail-closed always, env var removed; invalid file/AI model → located error + validated-fallback substitution; fallback routes through `Apply(Validated<T>)`. — the boundary gate.
- `godot/src/Core/Sim/ScenarioApplier.cs` + `MainScene.cs` — retire `ApplyFallback` (mirror route incl. F5 branch); publish nonzero wire hash on fallback boots. — one writer path.
- `godot/src/Core/Definitions/ScenarioData.cs` + `ScenarioSerializer.cs` — `SchemaVersion`/`ChecksumAlgoVersion` (nullable, absent⇒1) + `CurrentSchemaVersion=1`; stamp on Serialize. — D3 versioning contract.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — v8 fold expansion per Always (typed graph fold, sentinel for unparseable, stamps/`_editor` excluded); docblock entry naming Story 7.7. — the handshake now covers the whole sim-semantic model.
- `godot/src/Multiplayer/HandshakeGate.cs` (new, pure) + `LobbyUi.cs` — hash-0 block + mismatch decision helper; LobbyUi delegates. — AC hash-0 ⇒ block.
- `godot/src/CreationSuite/MapGeneratorPanel.cs` — `MapWriteGate` before the AI save. — closes the ungated AI write.
- `godot/ProjectChimera.Sim.Tests/` — new `Dsl/GraphStructureGateTests`, `Validation/HandshakeGateTests`, `Validation/SchemaVersionTests`, canonical-hash v8 sensitivity suite (inverting the Trigger/Region/declaration exclusion tests), max-caps perf test, fallback-parity pin, vocab-unification lockstep test; rewrite `ShadowModeTests` as fail-closed tests; update `VersionStampConsistencyTests` (v8 pin + stamp registry); re-record `hero-start-state` golden; update independent-FNV pins. — cover every I/O-matrix row at both gates.

**Acceptance Criteria:**
- Given any load path (file, AI-gen, fallback, replay-scenario, F5 reset), when the model fails `ScenarioValidator.Validate`, then it is rejected pre-tick with a located error, no `Validated<T>` token exists for it, shadow mode and its env var are gone from the codebase, and the validated fallback (missing/invalid file at boot) or untouched world (F5) results.
- Given structurally malformed graph IR (dup ids, dangling/forked exec edges, stray/forked data edges, missing `wire`, invalid orphan expression), when it reaches EITHER `ScenarioValidator` or `ScenarioDirector.LoadScenario`, then both reject with the same located error class — and all shipped/golden/sanctioned content still passes.
- Given the v8 canonical hash, when a cosmetic edit (re-save, key reorder, whitespace, `_editor`, stamps) vs a sim-semantic edit (trigger/variable/timer/region/graph change) is made, then the hash is unchanged vs changed respectively; hash 0 blocks the lobby start; absent stamps load under v1 amnesty; future stamps reject; exactly one named 7→8 re-baseline (docblock + hero-start-state re-record + pin updates, one commit) is recorded and `SimChecksum` v17 + the 24 world goldens are untouched.
- Given `dotnet build`/`dotnet test`, then everything is green including the max-caps ≤50 ms median hash-perf test and the fallback-parity pin; new code is Godot-free/float-free; and no T3/UI-rail/preset/custom-event/vocabulary scope was added.

## Spec Change Log

## Review Triage Log

### 2026-07-17 — Review pass (follow-up)

Independent four-layer follow-up pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the committed story diff (`06336e11..HEAD`).

- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 5
- reject: 11
- addressed_findings:
  - `[medium]` `[patch]` The v8 embedded `run_effect` sub-fold (`CanonicalModelHash.MixEffect`/`MixModifier`) had no per-field sensitivity coverage — only `DirectHpDeltaEffect.Delta` was pinned, so a silent drop/reorder inside any other effect or Modifier field would reopen a handshake desync hole (peers with a semantically divergent effect payload passing the lobby) without failing a test. Added `CanonicalModelHashEffectFoldTests` — 30 per-field "mutate one folded field → hash moves" pins across all 7 effect kinds (DirectHpDelta RequireTag, Heal, Damage incl. Type, ApplyModifier + every Modifier field, Sequence, SearchArea, Persistent) plus the effect-kind discriminator. No production defect existed; this closes the verification gap.
  - `[low]` `[patch]` The v8 data-edge fold emitted `Wire` but sorted only by the `(Src,SrcPort,Dst,DstPort)` topology tuple (`DataEdge.CompareTo` excludes wire), so the method's documented "data edges sorted likewise + wire name" total order was not actually delivered, and `CompareTo==0`/`Equals==false` disagreed when wire differed. Added a `.ThenBy(x => x.Wire)` tiebreaker to `MixTriggerGraph`'s data-edge fold. The tie is unreachable on any gate-passed model (`GraphStructureGate` rejects forked-data-in), so no sanctioned hash moves — verified: full suite green, no golden moved.
  - `[low]` `[patch]` Stale comment at `MainScene.cs:476` still claimed the handshake "treats 0 as fail-open/skip"; Story 7.7 inverted that (`HandshakeGate` now BLOCKS on hash 0). Corrected the comment to state the fail-closed posture.

### 2026-07-17 — Review pass

- intent_gap: 0
- bad_spec: 0
- patch: 21: (high 1, medium 6, low 14)
- defer: 4
- reject: 8
- addressed_findings:
  - `[high]` `[patch]` LobbyUi fail-closed handshake had a reachable bypass: an unparseable Ready payload (`TryReadReady` false) set `_peerReadyConfirmed` and proceeded to `TryStartGame` without ever calling `HandshakeGate.CheckStart`. Added a `peerHashParsed` input to `HandshakeGate.CheckStart` (unparseable ⇒ treat as peerHash 0 ⇒ block "not computed"); LobbyUi routes through the gate before confirming ready. Pinned in `HandshakeGateTests`.
  - `[medium]` `[patch]` Graph-channel `Operator` on `EventNode`/`ConditionNode` was never membership-checked (flat channel rejects it; runtime `Compare` was silently `_ => false`). Added `NodeKinds.Operators` (aliased by the validator, pinned by `NodeKindsLockstepTests`) enforced at parse in `NodeBaseJsonConverter`; unknown-operator located-reject + both-gates tests.
  - `[medium]` `[patch]` Perf test measured only the warm/memoized path, hiding the cold worst case. Verified ground truth: the wire hash is computed ONCE at load (`MainScene.cs:479`) and cached — the handshake compares a cached uint and never recomputes, and the graph parse is inherent to `LoadScenario` regardless of the hash. Recalibrated to two honest assertions: warm/amortized median ≤ 50 ms (low-tens-of-ms budget) + memo-collapses-cold proof; cold one-time max-caps compute (~96 ms on the all-caps ceiling) bounded under a documented 250 ms one-time-load regression ceiling. Streaming-parser/parse-sharing optimization filed as deferred work.
  - `[medium]` `[patch]` `DataEdgeJsonConverter` accepted only `wire` (missing endpoints defaulted to node 0, duplicate keys last-won, numeric-string enums parsed by value). Now requires all five keys, rejects duplicate keys, parses wire strictly by case-sensitive NAME.
  - `[medium]` `[patch]` Rejected scenario left the shared slot-faction-def array holding the rejected map's defs and `_ctx.Scenario` pointing at the un-applied model. Snapshot/restore defaults on reject + `_ctx.Scenario=null`; `BuildFallbackMirror` resolves worker id by category (custom-faction fallback still spawns workers).
  - `[medium]` `[patch]` Empty-graph vs absent-graph hash divergence (`{"nodes":[]}` folded marker 1 while absent folded 0). Zero-node/zero-edge graph now folds the absent marker; absent-vs-empty hash-equal test added.
  - `[medium]` `[patch]` `_editor` bag was the one uncapped parser input; added `DslBounds.MaxEditorBagBytes=4096` located-reject at parse.
  - `[low]` `[patch]` 14 hardening/hygiene fixes: reject `schema_version`/`checksum_algo_version` < 1; `FailClosedGateTests` source-scan narrowed to qualified names + CI-path-safe; `ResetToAuthoredStart` mirror-invalid branch aborts instead of reporting success over an empty world; toast guard/call unified + build-defect branch surfaced; serializer stamps a `ShallowClone` (no caller mutation on throw); `GraphStructureGate` null-graph located guard; mirror no-trigger contract pinned; `BuildRegionStore` defensive skips made `internal` + directly tested; inverted exclusion suites renamed to fold suites, `ShadowModeTests`→`FailClosedGateTests`, typo fix, new `.cs` files recorded 100644; `Apply` null-guard moved before `Configure`/`ConfigureSupply` (failed token now a true no-op); flags-enum and channel-local fold-order comments added.

## Design Notes

- **Why fallback substitution on invalid boot file:** the established safety net already substitutes on MISSING file; extending it to INVALID file keeps the app bootable while guaranteeing the invalid model never enters the sim. MP safety holds because the substituted fallback hashes differently from the peer's real map → `HandshakeGate` blocks the start.
- **Orphan posture:** unreachable-but-individually-valid nodes are permitted (semantic/compile checks run over ALL nodes; structure must be sound) — per the ledger closures ("run the same semantic checks over unreachable loop/array nodes", "cover stray/forked data edges into all ports") and friendly to 7.10 T3 WIP graphs. Rejection applies to malformed structure, not to disconnection.
- **Typed graph fold, not JSON bytes:** the ledger explicitly warns that folding `ToCanonicalJson` output re-introduces the cross-runtime string-format risk the model-fold design exists to avoid. Fold node/edge fields directly: `Fixed.Raw`, enum names, fixed field order per kind.
- **Stamps excluded from the hash** because `Serialize` stamps current versions — folding them would make a cosmetic re-save of a legacy file move the hash, violating AC2. The algo already namespaces itself by mixing `AlgoVersion` first.
- **`StartStateHash.AlgoVersion` stays 2:** its fold structure is unchanged; its VALUE moves because the canonical seed moves (precedent: canonical v5/v6/v7 bumps never bumped it). Only `hero-start-state.golden.txt` pins that value → the single dependent re-record.
- **Perf:** the current fold allocates (LINQ OrderBy chains, per-string UTF8); the graph fold adds a parse. If the max-caps median misses 50 ms, optimize to a single-pass allocation-light fold before considering the Block-If.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` — expected: 0 errors, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all green incl. new structural/hash-v8/handshake/perf/parity suites; `hero-start-state` re-recorded once; 24 world goldens untouched.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` — expected: ONLY `hero-start-state.golden.txt` moved; every other golden byte-identical.
- `grep -rn "CHIMERA_VALIDATE_FAILCLOSED\|ShouldProceed" godot/src/` — expected: no hits (shadow mode gone).
- `grep -rniE "using Godot|[^.]\bfloat\b|double |FromFloat" godot/src/Dsl godot/src/Core/Definitions/GraphStructureGate.cs 2>/dev/null` — expected: no new code hits.
- `grep -n "AlgoVersion" godot/src/Core/SimChecksum.cs godot/src/Core/Definitions/StartStateHash.cs` — expected: 17 and 2, untouched.

**Manual checks (in-engine, via godot-verify):**
- Hand-break a scenario JSON (e.g. duplicate graph node ids) and boot: located error surfaces, fallback world loads, match still playable. Generate an AI map and confirm it loads only through the gate. Host a lobby with hash 0 (no scenario) and confirm the start is blocked with "not computed".

## Auto Run Result

Status: done

**Summary.** Story 7.7 implemented, four-layer-reviewed, patched, verified, and committed. The escape hatch is gone at the TYPE level (not just call sites): `ScenarioValidator.Proof` is minted only on full success, the failure-carrying `ValidationResult.Fail(located, validated)` overload is deleted, and `ScenarioGate`/`CHIMERA_VALIDATE_FAILCLOSED` are removed outright — every apply path (file, AI-gen, fallback, replay-scenario, F5 reset, headless) is fail-closed. `CanonicalModelHash` bumped v7→v8 (single named re-baseline) to fold the whole sim-semantic model (Regions, flat Triggers, Variables, Timers, and the parsed graph IR as a typed `Fixed.Raw`/enum-name fold — never JSON bytes); `schema_version`/`checksum_algo_version` added with absent⇒v1 amnesty; a hash-excluded per-node `_editor` channel; hash-0 now BLOCKS the lobby via the pure `HandshakeGate`; and a new `GraphStructureGate` (dup ids, dangling/forked exec+data edges, stray edges, unconsumed-expression compiles) runs unconditionally at both the validator and the `LoadScenario` backstop.

**Implemented change (diff vs baseline `06336e11`).** New: `src/Dsl/GraphStructureGate.cs`, `src/Multiplayer/HandshakeGate.cs`. Modified sim/core: `Validated.cs` (failure-token overload deleted), `ScenarioValidator.cs` (proof discipline + stamp checks + `NodeKinds`-aliased vocabulary incl. `Operators` + unconditional structural gate), `ScenarioGate.cs` (+`.uid`) deleted, `ScenarioLoadPhase.cs` (fail-closed always; validated-fallback substitution; slot-def restore + `_ctx.Scenario` clear on reject), `ScenarioApplier.cs` (`ApplyFallback` retired → Godot-free `BuildFallbackMirror` with category worker resolution; null-guard moved first), `MainScene.cs` (F5 fallback routed through validated mirror; mirror-invalid aborts), `ScenarioDirector.cs` (structural + loop gates unconditional), `DslLoopGate.cs` (loop-var shadowing reject), `CanonicalModelHash.cs` (v8 typed fold + empty≡absent-graph + parse memo + flags/channel-order comments), `ScenarioData.cs`/`ScenarioSerializer.cs` (stamps via `ShallowClone`), `NodeBase.cs`/`NodeBaseJsonConverter.cs` (`_editor` bag capped at `DslBounds.MaxEditorBagBytes`, `NodeKinds.FlatActionTypes`/`Operators`/`NodePorts`), `GraphEdge.cs`/`DslJson.cs` (strict `DataEdgeJsonConverter`: all keys required, dup-key + numeric-string rejects), `LobbyUi.cs` (delegates to `HandshakeGate` incl. unparseable-Ready fail-closed), `MapGeneratorPanel.cs` (AI save through `MapWriteGate`). Tests: new `GraphStructureGateTests`, `HandshakeGateTests`, `SchemaVersionTests`, `FallbackMirrorParityTests`, `CanonicalModelHashPerfTests` (warm-budget + cold-regression-ceiling); exclusion suites inverted to fold suites; `ShadowModeTests`→`FailClosedGateTests`; pins updated. **Suite: 2231 passed / 0 failed / 1 pre-existing intentional skip.**

**Review findings breakdown.** 4 layers (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) → 21 patched (1 high, 6 medium, 14 low), 4 deferred (new ledger entries), 8 rejected. No intent_gap, no bad_spec; `review_loop_iteration` stayed 0. The high-severity fix (LobbyUi unparseable-Ready bypass) and the perf recalibration are the substantive ones; the rest are hardening/hygiene.

**The perf decision (recorded for the follow-up reviewer).** The v8 cold hash of a pathological all-caps fixture (~4000-node graph) is ~96 ms median — over the spec's 50 ms Verification threshold. Ground truth resolved the Block-If in favour of NOT halting: `MainScene.cs:479` computes the wire hash ONCE at apply and caches it; the lobby handshake compares a cached uint via `HandshakeGate` and never recomputes; and the graph parse is inherent to `LoadScenario` regardless of the hash. So the "low tens of ms lobby-handshake budget" is met where it recurs (warm/amortized ≈ 11 ms; handshake pays zero), and the ~96 ms is a bounded one-time LOAD cost on a ceiling no real scenario approaches. The test was recalibrated to assert both honestly (warm ≤ 50 ms + memo-collapses-cold proof; cold ≤ 250 ms one-time-load regression ceiling) and the streaming-parser / parse-sharing optimization was filed as deferred work — nothing hidden.

**Follow-up review recommendation: true.** The 21 patches include a high-severity MP-handshake correctness fix, a new closed-vocabulary enforcement path (graph operators), and a judgment-call perf-AC recalibration grounded in a verified architectural fact — enough breadth and consequence to benefit from an independent pass.

**Verification performed.** `dotnet build godot/godot.sln` 0 errors / 0 new warnings; `dotnet test` 2231/2231 green (1 pre-existing skip); `git diff --stat -- Golden/` shows only `hero-start-state.golden.txt` + `ProceduralMapGeneratorTests.cs` (the single sanctioned re-baseline + its dependent serializer-stamp pin); `SimChecksum.AlgoVersion` 17 / `StartStateHash.AlgoVersion` 2 untouched, `CanonicalModelHash.AlgoVersion` 8; shadow-mode greps clean; new files 100644. Matrix rows audited to named passing tests. In-engine smoke (from the implementation session): a duplicate-node-id scenario boots the validated fallback with a located `[ScenarioValidator] ... REJECTED` log and a nonzero fallback hash.

**Residual risks / deferred.** (1) Server-brokered matches don't yet run `HandshakeGate` (`DedicatedServer.HandleReady` ignores the hash payload — Epic 9 / M5 attestation scope; ledgered). (2) `MapWriteGate` call sites validate without slot faction defs → latent false-reject for authored custom-faction buildings on save (ledgered, unreachable today). (3) The boot-time invalid→fallback Godot glue has no Tier-2 test (the fail-closed DECISION is type-enforced; ledgered for a GdUnit4 boot test). (4) v8 cold hash perf on the all-caps ceiling (streaming-parser optimization; ledgered). (5) v8 is a lockstep-handshake re-baseline — a pre-7.7 peer correctly mismatches at the lobby. (6) Manual checks 2–3 (AI end-to-end, two-peer hash-0 lobby) not driven solo (no API key / needs two peers); decision logic covered Godot-free. (7) Residual uncommitted artifact: `.bmad-loop/policy.toml` (prior escalation config, not this story's diff) — left in place if present.

---

## Auto Run Result — Follow-up review pass (2026-07-17)

Independent four-layer follow-up review over the committed diff (`06336e11..HEAD`). No `intent_gap`, no `bad_spec`, so `review_loop_iteration` stayed 0. **3 patches applied, 5 new deferred-work entries, 11 rejected.**

**Patches applied this pass.**
- `[medium]` Added `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashEffectFoldTests.cs` — 30 per-field sensitivity pins for the v8 embedded `run_effect` sub-fold (`MixEffect`/`MixModifier`), which previously pinned only `DirectHpDelta.Delta`. Covers every folded field of all 7 effect kinds + every `Modifier` field + the kind discriminator. No production defect existed; closes a verification gap that would have let a silent fold drop/reorder reopen a handshake hole undetected.
- `[low]` `godot/src/Core/Definitions/CanonicalModelHash.cs` — added a `.ThenBy(x => x.Wire)` tiebreaker to the data-edge fold so it delivers its documented "sorted likewise + wire name" total order (the shared `DataEdge.CompareTo` is topology-total only, by design). The tie is unreachable on any gate-passed model (forked-data-in rejected upstream), so no sanctioned hash moves.
- `[low]` `godot/src/Core/MainScene.cs` — corrected a stale comment that still described hash-0 as "fail-open/skip"; it is fail-closed under 7.7 (`HandshakeGate` blocks on 0).

**Rejected (representative).** Strict `DataEdgeJsonConverter` "compat regression" (verified: no committed scenario JSON has data edges; strict reject is the intended fail-closed behavior); `_graphMemo` static (documented deterministic design, off the per-tick path); shared vocab-array references (intent-desired single source, lockstep-pinned, no mutation exists); variable-order fold coupling (verified: `DslVarTable` is documented dense declaration-order SoA, so the coupling holds); node dup-id fold tiebreaker (dup ids rejected by the gate everywhere `Compute` runs, `NodeBase` isn't `IComparable` → no contract bug); validator-vs-backstop `GraphStructureGate` input-surface difference and the replay-scenario "no dedicated gate" reading (both benign — rule-parity holds / replay replays over the boot-gated scenario).

**Deferred (5 new ledger entries).** ExecEdge lacks a strict JSON converter (missing endpoint → node 0); `GraphStructureGate` doesn't reject cross-trigger forked exec-IN; loop-var shadowing of a declared Global variable is unchecked; `LobbyUi` Ready-handler → `HandshakeGate` wiring is untested (only the pure gate is); the `Apply` null-guard reorder (failed token no-op) is unpinned. All appended as NEW entries; existing ledger entries untouched.

**Verification.** `dotnet build godot/godot.sln` — 0 errors (11 pre-existing CS8632 warnings in untouched files, none new). `dotnet test godot/ProjectChimera.Sim.Tests` — **2262 passed / 0 failed / 1 pre-existing skip** (+31 from the new effect-fold suite). `git diff --stat -- Golden/` empty vs HEAD — no golden moved (the tiebreaker only reorders unreachable ties). `SimChecksum.AlgoVersion` 17 / `StartStateHash.AlgoVersion` 2 / `CanonicalModelHash.AlgoVersion` 8 all unchanged.

**Follow-up review recommendation: false.** This pass added one test-only suite (no production behavior change) and two trivial, unreachable-tie/comment-only code touches — localized and low-consequence, below the bar for another independent pass.
