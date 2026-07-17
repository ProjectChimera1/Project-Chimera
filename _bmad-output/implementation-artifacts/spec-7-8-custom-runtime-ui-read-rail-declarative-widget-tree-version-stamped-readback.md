---
title: 'Story 7.8: Custom runtime UI read rail — declarative widget tree + version-stamped readback'
type: 'feature'
created: '2026-07-17'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: true
baseline_revision: '23d00869b3736e90414928db795782d42f1242f9'
final_revision: '96662cb'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Creators can author DSL variables/timers (7.3) but have no way to *show* live game state to the player. There is no custom-UI schema in `ScenarioData`, no presentation surface bound to DSL variables, and no publish path from the sim to widgets. The trigger/DSL model must gain a declarative, closed-vocabulary widget tree that renders live scoreboards/counters/timers **without any string or presentation type ever entering the deterministic tick**, and that a multiplayer host can prove identical before start.

**Approach:** Add a Godot-free closed-vocabulary widget-tree schema (`CustomUiTree`) to `ScenarioData`, serialized through a closed-registry converter (mirroring `NodeBaseJsonConverter`), folded into `CanonicalModelHash` (AlgoVersion 8→9, one golden re-baseline) so divergent UIs refuse to start, and validated + cap-checked at load through a shared `CustomUiGate` (mirroring `GraphStructureGate`) invoked by both `ScenarioValidator` and the `ScenarioDirector.LoadScenario` backstop. On the runtime side, add a double-buffered, per-variable **version-stamped `DslVarReadback`** — a *copy* of already-checksummed `DslVarTable` state published once per tick at the tick boundary and explicitly **excluded from `SimChecksum`**. Presentation `CustomUiBridge` pulls the back buffer in `_Process` and re-formats a widget only when its bound variable's version changes; formatting (int→string, Fixed→mm:ss) happens presentation-side. A `CustomUiBuilderPanel` authoring surface writes the tree.

## Boundaries & Constraints

**Always:**
- **Sim/definitions layer stays Godot-free and float-free.** The widget-tree schema, converter, gate, caps, and readback carry **no `using Godot;`** and no `float`/`double` in stored numerics — positions/sizes are int canvas units; any fractional/timer value is `Fixed` (via `.Raw`). Only the renderer/builder under `src/UI`, `src/CreationSuite`, and the new bootstrap phase use Godot `Control`/`CanvasLayer` types.
- **Strings never enter the tick.** The readback carries only typed raw values (`type`, `raw0`, `raw1`, `version`); all int→string / Fixed→mm:ss formatting is presentation-side in the renderer.
- **Readback is a copy and is NOT folded into `SimChecksum`.** It is derived from already-checksummed `DslVarTable` state; it is never passed to `SimChecksum.Compute`, and `SimChecksum.AlgorithmVersion` stays **17** with the 24 world goldens byte-identical. Add a one-line "presentation-only readback — not folded" note in the `SimChecksum` summary per the established exclusion convention. Because it is unfolded, a UI mismatch *cannot* desync (AR-32 read rail).
- **Widget tree is covered by the canonical hash.** Fold `CustomUi` into `CanonicalModelHash` via a typed recursive walk (mirror `MixTriggerGraph`/`MixGraphNode`: present/absent marker, count prefix, typed `switch` on the closed kind, `Fixed` via `.Raw`, enums by name, fixed field order, recurse children with per-child present bit). Never fold serialized JSON bytes. The per-widget `_editor` bag is excluded **by construction** (the typed walk reads typed fields only). Bump `CanonicalModelHash.AlgoVersion` 8→9, add the v9 docblock entry naming Story 7.8, and record exactly **one** named re-baseline (re-record `hero-start-state.golden.txt`, update the AlgoVersion pin) in a single commit.
- **Closed vocabulary, fail-closed parse.** The only widget kinds are Panel, Label, Counter, ProgressBar, Timer, Leaderboard, FloatingText, ItemList. `WidgetBaseJsonConverter` dispatches on a hardcoded `kind` registry (no `[JsonPolymorphic]`, no reflection), rejects unknown/duplicate properties per kind (`RejectUnknownProperties`), allow-lists a size-capped `_editor` bag (`DslBounds.MaxEditorBagBytes`), and emits deterministic canonical output. An unknown kind fails closed at parse with a located error naming it.
- **Caps rejected AT LOAD, never clamped.** Named consts `MaxWidgetCount=256`, `MaxWidgetDepth=8`, `MaxListRows=64` live in a `DslBounds`-style home; `CustomUiGate` rejects violations with a located error naming the constant. The renderer additionally *asserts* these at runtime and refuses to render an over-cap tree rather than truncating (belt-and-suspenders; the load gate makes this unreachable on sanctioned content).
- **Every BindVar resolves + type-matches at load.** `CustomUiGate.Check(tree, declaredVarInfo, declaredArrayInfo)` resolves each `{variable}` binding against the validator's declared-variable registry and rejects an unresolved/type-mismatched bind with a located path (`scenario.custom_ui.widgets[i]...`). Scalar-display widgets (Label/Counter/ProgressBar/Timer) bind Int/Fixed; Leaderboard/ItemList bind `Array<scalar>`. The gate is the ONE shared implementation invoked by both `ScenarioValidator` (as `ValidationResult.Fail`) and `ScenarioDirector.LoadScenario` (backstop) — parity by construction.
- **Publish once per tick at the tick boundary.** `DslVarReadback.Publish(table, tick)` runs exactly once per completed tick, reading the FINAL post-tick `DslVarTable` state (ScenarioDirector ticks last), bumping a per-variable monotonic `version` only when that variable's raw value changed, then swapping the double buffer so presentation always reads a consistent snapshot. It writes only the readback — never any sim state.

**Block If:**
- The intended closed widget vocabulary or a cap value contradicts the epic's stated set/limits in a way that changes an observable acceptance outcome and the epic gives no basis to choose. (None expected — vocabulary and caps are fixed by Epic 7 context.)

**Never:**
- No scripting escape hatch, no `[JsonPolymorphic]`/`[JsonDerivedType]`, no reflection-based widget construction.
- No write rail / interactive Button behavior, no `DslEventCommand`, no lockstep-bus mutation, no replay-v2 — that is Story 7.9. (A `Button` widget kind is **out of scope here**; this story is read-only display.)
- No T3 node-graph editor (7.10), no win-condition presets (7.11), no new sim variables/timers beyond what 7.3 delivers.
- Do not fold the readback into `SimChecksum`; do not bump `SimChecksum.AlgorithmVersion`; do not move any world golden. Do not clamp caps at runtime.
- No full drag-and-drop direct-manipulation canvas editor is required for acceptance — a functional palette+inspector builder with a live 16:9 preview that produces a valid persisted tree is sufficient; richer manipulation is optional polish.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Live counter update | Counter bound to Int var `score`; `score` changes on tick N | Readback version for `score` bumps at tick N boundary; bridge re-formats and displays new value next `_Process` | No error |
| Unchanged tick | Bound var value identical across ticks | Version unchanged; widget NOT re-formatted (skip) | No error |
| Timer widget | Timer bound to Fixed/Int-tick var | Presentation formats via Fixed→mm:ss helper; no string in tick | No error |
| Leaderboard/ItemList | Array<scalar> var with ≤64 rows | Data-bound repeater renders one row per element | No error |
| Unresolved/mismatched bind | Widget binds undeclared name or wrong type | Load reject: located error `scenario.custom_ui.widgets[i].bind ...`; scenario refuses to start | Fail-closed at both gates |
| Over-cap tree | >256 widgets, or depth >8, or ItemList >64 rows | Load reject naming the cap constant | Fail-closed; renderer also asserts, never clamps |
| Unknown widget kind | JSON `"kind":"WebView"` | Parse reject: located error naming the kind | Converter fail-closed |
| Duplicate widget id / stray property | Malformed widget JSON | Located parse/gate reject | Fail-closed |
| Divergent UIs (MP) | Two peers with different widget trees | Different v9 canonical hash → `HandshakeGate` blocks start | Fail-closed |
| Cosmetic vs semantic edit | `_editor` move / re-save vs bind change | Hash unchanged vs changed respectively | No error |
| Readback not in checksum | Custom UI present/absent/toggled | `SimChecksum` byte-identical; cannot desync | No error |
| Absent custom_ui | Scenario with no `custom_ui` | Serializes with no key (empty→null); round-trips absent; renderer no-op | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/CustomUiTree.cs` (new) — pure-C# closed-vocabulary schema: `CustomUiTree` root (`Widget[]` + 16:9 canvas ref), `WidgetBase` (id, `AnchorPoint` 9-point enum, int offset/size, optional `{variable}` visibility bind, `_editor` bag) + 8 kind subclasses. Godot-free, float-free.
- `godot/src/Core/Definitions/WidgetBaseJsonConverter.cs` (new) — closed-registry converter mirroring `godot/src/Dsl/NodeBaseJsonConverter.cs` (`kind` discriminator, `RejectUnknownProperties`, `_editor` allow-list @ `DslBounds.MaxEditorBagBytes`, deterministic `Write`).
- `godot/src/Core/Definitions/ScenarioData.cs` — add `[JsonPropertyName("custom_ui")]` omit-when-null `CustomUiTree? CustomUi` near the trigger/DSL blocks (`:545-584`); model on the `Regions`/`Variables` precedent. Excluded-vs-folded docblock context `:541-584`.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — register `WidgetBaseJsonConverter` in `_options.Converters` (`:36`); add empty→null normalization for `CustomUi` beside `:84-98`.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — add `MixCustomUi` typed recursive fold (mirror `MixTriggerGraph :413-477` / `MixGraphNode :481-593`); call in `Compute` in fixed order after `:238`; bump `AlgoVersion` 8→9 (`:103`); v9 docblock entry (`:90-102` template); `_editor` excluded by construction.
- `godot/src/Dsl/CustomUiGate.cs` (new) — shared rulebook `Check(tree, declaredVarInfo, declaredArrayInfo) -> string?` (first located error/null): caps, dup ids, anchor validity, recursive depth, bind resolve+type-match. Model on `godot/src/Dsl/GraphStructureGate.cs :7-46`.
- `godot/src/Dsl/DslBounds.cs` — add `MaxWidgetCount=256`, `MaxWidgetDepth=8`, `MaxListRows=64` (convention @ `:4-13`, alongside `MaxArrayCapacity :21`, `MaxEditorBagBytes :83`).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — add a present-when-non-null widget pass (~after triggers/graph `:887`, before authoring-only blocks) invoking `CustomUiGate.Check` wrapped in `ValidationResult.Fail`; reuse `declaredVarInfo :410-435` / `declaredArrayInfo :445`.
- `godot/src/Core/ScenarioDirector.cs` — invoke `CustomUiGate.Check` unconditionally in the `LoadScenario` backstop (parity with validator, per 7.7 GraphStructureGate precedent); publish `DslVarReadback` once per tick at end of its `Tick` reading final `_vars :34`.
- `godot/src/Core/Sim/DslVarReadback.cs` (new) — Godot-free double-buffered, per-variable version-stamped readback. `Publish(DslVarTable, tick)`; read API `TryGet(name) -> (DslValueType, raw0, raw1, version)` + array read. NEVER passed to `SimChecksum.Compute`.
- `godot/src/Core/Sim/SimulationHost.cs` — own the `DslVarReadback` instance (`:199-245` composition root); expose a read-only handle for presentation; wire ScenarioDirector to publish into it.
- `godot/src/Core/SimChecksum.cs` — add a one-line "readback not folded — presentation-only" note in the summary; assert-by-omission unchanged. `AlgorithmVersion` stays 17.
- `godot/src/UI/CustomUiBridge.cs` (new) — presentation renderer: reads readback in `_Process`, builds/updates a `Control` tree on the overlay layer, re-formats a widget only on bound-var version change, enforces 16:9 safe area, asserts caps. Reuse `ChimeraComponents` (`godot/src/UI/Components/ChimeraComponents.Surfaces.cs`: `Panel/Readout/Progress/Chip`) + `ChimeraListRow`. Construct-and-`Initialize` model = `godot/src/Core/Bootstrap/Phases/RenderingPhase.cs :27-45`.
- `godot/src/UI/WidgetFormat.cs` (new) — presentation-side `int→string`, `Fixed→mm:ss` (consolidate `MainScene.cs:1287` / `OnboardingPanel.cs:151` patterns; use `mono_tnum` token).
- `godot/src/Core/Bootstrap/Phases/CustomHudOverlayPhase.cs` (new) — new `ISetupPhase` after `HudPhase` in the `MainScene.cs :431` phase list; own a `CanvasLayer { Layer = N }` (TriggerEditorPanel `:152` precedent); construct `CustomUiBridge`, `Initialize(readback, tree)`; store handle on `SceneContext.cs`. Pump via `_ctx.CustomHud.Update()` in `MainScene._Process :701-765`.
- `godot/src/CreationSuite/CustomUiBuilderPanel.cs` (new) — widget-palette authoring builder (palette of 8 kinds, 9-point anchor selector, size/offset, `{variable}` bind dropdown filtered by declared-var type, visibility bind, live 16:9 preview, writes `ScenarioData.CustomUi`). Model on `TriggerEditorPanel.cs :150-216` + `ComponentGallery.cs`.
- `godot/ProjectChimera.Sim.Tests/` — new `Dsl/CustomUiGateTests`, `Definitions/CustomUiSerializationTests`, `Definitions/CanonicalModelHashCustomUiTests` (v9 sensitivity), `Sim/DslVarReadbackTests`; re-record `Golden/hero-start-state.golden.txt`; update `Meta/VersionStampConsistencyTests` (AlgoVersion 9). Precedent suites: `Dsl/GraphStructureGateTests`, canonical-hash fold suites.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/CustomUiTree.cs` (new) — define the Godot-free/float-free closed widget-tree schema (root + `WidgetBase` + 8 kinds, 9-point `AnchorPoint` enum, int canvas coords, `{variable}` binds, `_editor` bag). — the schema spine.
- `godot/src/Core/Definitions/WidgetBaseJsonConverter.cs` (new) + `ScenarioSerializer.cs` — closed-registry converter (kind discriminator, reject unknown/dup props, `_editor` allow-list+cap, deterministic write); register it; empty→null normalize `CustomUi`. — fail-closed round-trip, byte-identical when absent.
- `godot/src/Core/Definitions/ScenarioData.cs` — add omit-when-null `CustomUi` field on the `Regions` precedent. — persistence.
- `godot/src/Dsl/DslBounds.cs` + `godot/src/Dsl/CustomUiGate.cs` (new) — named caps; shared `Check` rulebook (caps/dup-ids/anchor/depth/bind-resolve+type-match) returning located errors. — the one-implementation gate.
- `godot/src/Core/Definitions/ScenarioValidator.cs` + `godot/src/Core/ScenarioDirector.cs` — invoke `CustomUiGate` from BOTH the validator (present-when-non-null, `declaredVarInfo`) and the `LoadScenario` backstop. — parity gate.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — `MixCustomUi` typed recursive fold; bump `AlgoVersion` 8→9; v9 docblock naming Story 7.8; `_editor` excluded. — handshake now covers custom UI.
- `godot/src/Core/Sim/DslVarReadback.cs` (new) + `SimulationHost.cs` + `ScenarioDirector.cs` — double-buffered per-variable version-stamped readback; publish once per tick at end of the last system's tick reading final variable state; host owns it; exclude from `SimChecksum`. — the read rail.
- `godot/src/Core/SimChecksum.cs` — documented "not folded" note; `AlgorithmVersion` untouched (17). — exclusion recorded.
- `godot/src/UI/CustomUiBridge.cs` (new) + `WidgetFormat.cs` (new) + `Core/Bootstrap/Phases/CustomHudOverlayPhase.cs` (new) + `SceneContext.cs` + `MainScene.cs` — renderer reading the readback, re-formatting only on version change, 16:9 safe-area, runtime cap assert; overlay phase + pump. — live presentation.
- `godot/src/CreationSuite/CustomUiBuilderPanel.cs` (new) — palette+inspector authoring builder writing a valid persisted tree with live 16:9 preview. — authoring surface.
- `godot/ProjectChimera.Sim.Tests/` — unit-test every I/O-matrix row: gate rejects (caps naming the const, unresolved/mismatched bind, unknown kind, dup id, over-depth); serialization round-trip incl. `_editor` + unknown-prop reject + absent-serializes-keyless; canonical-hash v9 sensitivity (widget-field change moves hash; `_editor`/re-save/absent-vs-empty do NOT; `AlgoVersion==9`); readback version-stamp (change bumps / unchanged doesn't / double-buffer snapshot consistency); `SimChecksum` AlgorithmVersion==17 + world goldens untouched; re-record `hero-start-state`; update `VersionStampConsistencyTests`. — full coverage at both gates.

**Acceptance Criteria:**
- Given a scenario whose widget tree binds a Counter/Leaderboard to a declared DSL variable, when the sim ticks and that variable changes, then the version-stamped `DslVarReadback` (a copy of already-checksummed state, published once per tick, NOT in `SimChecksum`) bumps that variable's version, the presentation widget re-formats only on version change, formatting happens presentation-side, and no string/presentation type enters the tick.
- Given any load path (file, AI-gen, fallback, replay-scenario, F5 reset) with a widget tree, when a BindVar is unresolved or type-mismatched, or the tree exceeds `MaxWidgetCount`/`MaxWidgetDepth`/`MaxListRows`, or a widget kind/property is unknown, then it is rejected pre-tick with a located error naming the offending element/cap by EITHER `ScenarioValidator` or `ScenarioDirector.LoadScenario` (identical error class), and all shipped/golden/sanctioned content still passes.
- Given the v9 canonical hash, when a cosmetic edit (`_editor`, re-save, key reorder) vs a sim-semantic widget edit (bind/kind/layout-field change) is made, then the hash is unchanged vs changed respectively; divergent widget trees produce different hashes so the lobby start is blocked; absent `custom_ui` serializes keyless and round-trips absent; and exactly one named 8→9 re-baseline (docblock + `hero-start-state` re-record + AlgoVersion pin, one commit) is recorded while `SimChecksum` v17 and the 24 world goldens are untouched.
- Given `dotnet build`/`dotnet test`, then everything is green including the new gate/serialization/hash-v9/readback suites; sim/definitions code is Godot-free and float-free; and no write-rail/Button/replay-v2/T3/preset scope was added.

## Spec Change Log

## Review Triage Log

### 2026-07-17 — Review pass

Independent four-layer pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full story diff (`23d00869..working tree`, tracked + untracked). Intent Alignment confirmed no material functional divergence — the diff faithfully implements the charitable superset and resolves both intent ambiguities sensibly (readback derives its own versions; director publishes / bridge consumes per the FogOfWarBridge referent). Every escalated finding was verified against the actual code before triage.

- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 3, low 3)
- defer: 3
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` **Per-player binds resolved the Neutral slot in all modes.** `CustomHudOverlayPhase.Run` hardcoded `localFaction: 0`; verified that `DslVarReadback.TryGetScalar` uses that argument directly as the per-player slot and `Faction.Neutral = 0` while the local player is `Faction.Player1 = 1` — so every PerPlayer-scoped `{variable}` bind showed the untouched initial value (offline and online). Fixed by late-binding the local faction: `CustomUiBridge.Initialize` now takes a `Func<int>` resolved live each frame, wired to `_ctx.Lockstep.LocalFaction` (the same handle `MatchChatOverlay` uses; defaults to Player1, set at `GoOnline`). Global binds were unaffected.
  - `[low]` `[patch]` **Tree container was not fail-closed for stray keys.** The widget-level `WidgetBaseJsonConverter` rejects unknown/duplicate properties, but `CustomUiTree` deserialized under default STJ (no `UnmappedMemberHandling.Disallow`), so an unknown key on the `custom_ui` object was silently dropped — inconsistent with the closed-vocabulary posture. Added `[JsonUnmappedMemberHandling(Disallow)]` to `CustomUiTree` (global options untouched) + a fail-closed test.
  - `[medium]` `[patch]` **v9 handshake fold had per-field sensitivity coverage for only 4 of ~13 folded fields.** Only `Bind/Kind/Anchor/X` were pinned; a future drop of any other folded field (`Y/W/H/VisibleBind/CanvasWidth/CanvasHeight/ProgressBar.Max/Leaderboard.Rows/ItemList.Rows/Label.Text/FloatingText.Text`) would silently reopen a divergent-UI desync hole with every test still green. Added 11 per-field `_MovesHash` pins (mirrors the Story 7.7 `CanonicalModelHashEffectFoldTests` remedy). No production defect existed.
  - `[medium]` `[patch]` **Neither load-gate adoption site was exercised with a bad tree.** `CustomUiGate` was unit-tested in isolation, but no test drove `ScenarioValidator.Validate` or `ScenarioDirector.LoadScenario` with a `CustomUi` tree, so removing/short-circuiting either gate wiring would ship green. Added `ScenarioValidatorCustomUiTests` (validator returns `Fail`, director throws a located `JsonException` on an unresolved-bind fixture; plus a passing control).
  - `[low]` `[patch]` **Read-rail wiring had no end-to-end ticking test.** `DslVarReadback` was tested directly on a hand-built table, but nothing drove the sim to prove the director actually publishes each tick, so a dropped `SetReadback`/`Publish`/`InitFromDeclarations` would silently leave the HUD dead. Added `ScenarioDirectorReadbackTests` — loads a scenario, drives one `Tick`, asserts the readback reflects the post-tick value with a bumped version.
  - `[low]` `[patch]` **Stale `AlgoVersion` comments.** Six test files asserted `9` but kept Story 7.7's "bumped 7→8" wording; corrected the comments to the Story 7.8 8→9 (custom-UI fold) bump (asserted values untouched).

Deferred (3, logged in `deferred-work.md`): (1) `CustomUiBuilderPanel` is built but unreachable — never wired into a phase/editor menu/hotkey — plus an unfiltered bind dropdown and no nested-child authoring (the authoring half ships JSON-only; likely matures with Story 7.9); (2) `WidgetFormat` has no unit coverage and is not Tier-1-testable (presentation-only, lives in the Godot assembly); (3) `CustomUiGate` does not bound-check canvas/widget geometry (renderer defends; author-error hardening).

Rejected (9): folding the widget tree + the one 8→9 re-baseline into the handshake is explicitly mandated by the intent ("covered by scenarioHash; divergent UIs refuse to start"), so the three design critiques against it were dropped; the parse/hash recursion concern is bounded by STJ `MaxDepth` + the mandatory depth gate; the double-buffer "tearing" findings are false-criticals (publish and `CustomUiBridge.Update` both run on the single `_Process` main thread — no concurrency exists); and the unconditional-`CustomHud`-deref, hash-vs-serializer field-order, `ticksPerSecond` constant, and gate message-parity notes are convention-consistent or outcome-neutral.

### 2026-07-17 — Review pass (follow-up)

Independent four-layer follow-up pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full story diff (`23d00869..working tree`), triggered by the prior pass's `followup_review_recommended: true`. Intent Alignment confirmed the diff faithfully implements BOTH the definition and runtime read-rail readings with no functional divergence; the material finding is that the PRIOR pass's own F1 "fix" introduced a real per-player off-by-one. Every escalated finding was verified against the actual code (and the project's `SimSources.props` / float-free analyzer constraints) before triage.

- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 1, medium 0, low 5)
- defer: 2
- reject: 6
- addressed_findings:
  - `[high]` `[patch]` **Per-player binds read the NEXT player's slot (off-by-one) — the prior pass's F1 fix was inverted.** The DSL per-player store is 0-based with **slot 0 = Player1** (`set_variable`/`variable_comparison` pass the trigger's 0-based `Faction` field straight through as the slot, whereas engine-faction ops convert with `(Faction)(field+1)` — verified at `ScenarioDirector.cs:913/938/1131/1145`). `CustomHudOverlayPhase` passed the raw engine faction int (`Faction.Player1 = 1`) as that slot, so a local Player1 read **slot 1 = Player2** — offline it shows the untouched initial forever; online it shows the opponent's value. The prior pass conflated engine `Neutral=0` with the DSL slot space. Fixed by a new tested sim-side conversion `DslVarReadback.PlayerSlotForFaction(engineFaction) => engineFaction<=0 ? 0 : engineFaction-1`, wired into the phase getter, with the inverted comment corrected. Added `PlayerSlotForFaction_MapsEngineFactionToZeroBasedSlot` + `PerPlayer_LocalPlayerReadsOwnSlot_NotNextPlayers` (closing the prior "no per-player read coverage" gap at the one Tier-1-testable seam — the bridge itself is Godot-coupled).
  - `[low]` `[patch]` **Nested child widgets rendered double-offset.** `CustomUiBridge.BuildWidget` passed `node.Position` as a child's `parentOrigin`, but a Godot child `Control`'s `Position` is relative to its parent's local origin (0,0) — so every nested child was offset by the parent's own position twice (masked only when the parent sat at the safe-area origin). Fixed to pass `Vector2.Zero`.
  - `[low]` `[patch]` **ProgressBar bound to a Fixed variable lost sub-integer precision.** `WidgetFormat.Fraction` truncated a Fixed via `.ToInt()` before dividing, so a fractional `hp=50.5/100` filled to 0.50 and the bar stepped by whole units. Fixed to use `.ToFloat()` for the Fixed branch (presentation float is permitted in `WidgetFormat`); `MmSs` second-flooring is intended and unchanged.
  - `[low]` `[patch]` **v9 fold pinned every folded field except `Id`.** `MixWidget` folds `w.Id` but the otherwise-exhaustive per-field sensitivity suite had no `ChangedId_MovesHash`, so a future drop of the Id fold would ship green. Added the pin (consistent with the prior pass's 11-field remediation).
  - `[low]` `[patch]` **Double-buffer doc overclaimed thread safety.** `DslVarReadback`'s class doc said the snapshot is tear-free "even if a tick publishes concurrently"; with only two buffers that is true ONLY because publish and read both run single-threaded on `_Process`. Reworded to state the single-thread assumption explicitly (a reader holding a snapshot across two publishes would see its buffer reused) and clarified `volatile` guards only the reference read.
  - `[low]` `[patch]` **`WidgetFormat` doc claimed a consolidation that never happened.** Its summary said it "Consolidates the ad-hoc mm:ss patterns in `MainScene`/`OnboardingPanel`," but those inline sites are unchanged and not routed through it (and differ — `WidgetFormat` clamps negatives, they don't). Reworded to describe the actual (parallel, not consolidating) relationship.

Deferred (2 NEW ledger entries): (1) a repeater's `Rows` / a `ProgressBar`'s `Max` have no lower-bound gate, so an authored `rows:0`/`max:0` folds into the hash but is silently normalized at render (rows→64) — hash/render meaning diverge (distinct from the prior pass's generic geometry-bound-check entry); (2) the `custom_ui` array is fully parsed/allocated before `CustomUiGate` enforces `MaxWidgetCount` (project-wide parse-then-gate breadth class, latent DoS surface for untrusted content).

Rejected (6): per-player uses one version counter for all 8 slots (correctness-safe; extra re-formats negligible for a handful of HUD widgets); the `30`-ticks/sec `WidgetFormat` constant (outcome-neutral — currently correct); `CheckArrayBind`'s missing scalar-element check (**unreachable** — `DslLoopGate.CheckDeclarations` already rejects non-scalar array element types at declaration); `uint` version overflow (~4.5 yrs continuous change); and two findings already logged as prior-pass defers — `CustomUiBuilderPanel` unreachable, and `WidgetFormat` untested (the reviewers' "trivially testable in Sim.Tests" claim is **false**: `src/UI` is excluded from `SimSources.props`, and adding a `double`/`ToFloat`-using file to the shared sim source set would trip the release-gated float/double keyword ban — the existing defer's conclusion stands).

## Design Notes

- **Why the readback is net-new, not "modeled on FogOfWarBridge" literally:** FogOfWarBridge (and every other bridge) is a *live-reader* — it reads the sim store directly each frame with no buffering or versioning. The epic text "modeled on FogOfWarBridge" refers to the *construct-and-`Initialize`* wiring shape (RenderingPhase), not an existing double-buffer. The version-stamped double buffer genuinely does not exist and must be built: publish into the back buffer at the tick boundary, then swap the reference so `_Process` always reads a torn-free snapshot.
- **Version derivation without sim-side dirty tracking:** `DslVarTable` has no change signal. The readback computes versions itself: on `Publish`, compare each variable's current raw(s) to the last-published raw(s); if changed, increment that variable's `version`. This keeps all change-tracking on the presentation-only readback (unfolded) — no new sim state, no SimChecksum impact.
- **Publish site = ScenarioDirector end-of-tick (system [14], last):** the variables reach their final per-tick value only after the last system runs, so publishing there reads settled state exactly once per tick. Equivalent alternative: `SimulationLoop` right after `CurrentTick++` (the checksum hook line), made unconditional. Either satisfies "once per tick at the tick boundary" — pick the cleaner wiring; the invariant is final-post-tick-state, exactly once, writing only the readback.
- **Hash: typed fold, not JSON bytes** (per the epic's ledgered cross-runtime warning): fold widget fields directly (`Fixed.Raw`, enum names, fixed field order, count prefixes, per-child present bit). Bumping `AlgoVersion` 8→9 moves every scenario's canonical hash (AlgoVersion is folded first), which moves the `StartStateHash`-derived `hero-start-state.golden` value — that single golden is the one dependent re-record (`StartStateHash.AlgoVersion` stays 2; precedent: 7.7's v7→v8 bump).
- **Example widget JSON (canonical, `_editor` last):**
  `{"id":1,"kind":"Counter","anchor":"TopRight","x":-220,"y":24,"w":200,"h":48,"bind":"score","_editor":{...}}`
  Renders top-right in the 16:9 safe area, re-formats `score` (Int) via `WidgetFormat` only when its readback version changes.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` — expected: 0 errors, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all green incl. new `CustomUiGate`/serialization/`CanonicalModelHashCustomUi`(v9)/`DslVarReadback` suites; `hero-start-state` re-recorded once.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` — expected: ONLY `hero-start-state.golden.txt` moved; every other golden byte-identical.
- `grep -n "AlgoVersion" godot/src/Core/Definitions/CanonicalModelHash.cs` — expected: 9.
- `grep -n "AlgorithmVersion" godot/src/Core/SimChecksum.cs` — expected: 17, untouched.
- `grep -rniE "using Godot|[^.]\bfloat\b|double |FromFloat" godot/src/Core/Definitions/CustomUiTree.cs godot/src/Core/Definitions/WidgetBaseJsonConverter.cs godot/src/Dsl/CustomUiGate.cs godot/src/Core/Sim/DslVarReadback.cs 2>/dev/null` — expected: no hits (sim/definitions layer Godot-free/float-free).
- `grep -rniE "DslEventCommand|EnqueueDslEvent|class .*Button.*Widget|replay.*v2" godot/src/Core godot/src/Dsl 2>/dev/null` — expected: no hits (no write-rail scope).

**Manual checks (in-engine, via godot-verify):**
- Author a widget tree (Counter + ProgressBar + Timer + Leaderboard) in `CustomUiBuilderPanel`, save, playtest: widgets render inside the 16:9 safe area and update live as bound DSL variables change; a timer shows mm:ss. Hand-edit the JSON to bind an undeclared variable and boot: located error surfaces, fallback world loads. Hand-edit to exceed 256 widgets: located cap-named reject.

## Auto Run Result

Status: done

**Summary:** Implemented the Story 7.8 custom-UI read rail end to end — a Godot-free closed-vocabulary widget-tree schema (`CustomUiTree` + 8 kinds) persisted in `ScenarioData`, serialized through a closed-registry `WidgetBaseJsonConverter` (fail-closed on unknown kinds/props, `_editor` allow-list), folded into the MP handshake via `CanonicalModelHash` (AlgoVersion 8→9, one `hero-start-state` re-baseline), and validated + cap-checked at load through a shared `CustomUiGate` invoked by both `ScenarioValidator` and the `ScenarioDirector.LoadScenario` backstop. Runtime side: a double-buffered, per-variable version-stamped `DslVarReadback` (owned by `SimulationHost`, published once per tick from `ScenarioDirector.Tick`, never folded into `SimChecksum` — cannot desync) that a presentation `CustomUiBridge` reads in `_Process`, re-formatting a widget only when its bound variable's version changes; a `WidgetFormat` helper does int→string / Fixed→mm:ss presentation-side; a `CustomUiBuilderPanel` authoring surface produces valid trees. Named caps `MaxWidgetCount=256`/`MaxWidgetDepth=8`/`MaxListRows=64` reject at load and are asserted (never clamped) at runtime.

**Files changed (production):**
- `godot/src/Core/Definitions/CustomUiTree.cs` (new) — Godot-free/int-only closed widget schema (root + `WidgetBase` + `AnchorPoint` + 8 kinds); `[JsonUnmappedMemberHandling(Disallow)]` on the tree (review patch).
- `godot/src/Core/Definitions/WidgetBaseJsonConverter.cs` (new) — closed-registry converter (kind discriminator, reject unknown/dup props, `_editor` allow-list capped at `MaxEditorBagBytes`, deterministic write, recursive children).
- `godot/src/Core/Definitions/ScenarioData.cs` — omit-when-null `custom_ui` field.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — registered the converter; empty→null normalization (keyless when absent/empty).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — `MixCustomUi`/`MixWidget` typed recursive fold; `AlgoVersion` 8→9 + v9 docblock; `_editor` excluded by construction.
- `godot/src/Dsl/DslBounds.cs` — `MaxWidgetCount=256`, `MaxWidgetDepth=8`, `MaxListRows=64`.
- `godot/src/Dsl/CustomUiGate.cs` (new) — shared caps/dup-id/anchor/depth/bind-resolve+type-match rulebook (first located error).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — present-when-non-null `CustomUiGate.Check` pass (reuses `declaredVarInfo`/`declaredArrayInfo`).
- `godot/src/Core/ScenarioDirector.cs` — unconditional `CustomUiGate` backstop; owns/re-inits a `DslVarReadback`; publishes once per tick reading final `_vars`.
- `godot/src/Core/Sim/DslVarReadback.cs` (new) — double-buffered per-variable version-stamped read rail; never passed to `SimChecksum.Compute`.
- `godot/src/Core/Sim/SimulationHost.cs` — owns `Readback`, wires `ScenarioDirector.SetReadback`, clears on reset.
- `godot/src/Core/SimChecksum.cs` — "readback not folded — presentation-only" note; `AlgorithmVersion` stays 17.
- `godot/src/UI/CustomUiBridge.cs` (new) — presentation renderer; live local-faction resolution via `Func<int>` getter (review patch); re-formats only on version change; 16:9 safe area; runtime cap assert.
- `godot/src/UI/WidgetFormat.cs` (new) — int→string / Fixed→mm:ss / fraction helpers.
- `godot/src/Core/Bootstrap/Phases/CustomHudOverlayPhase.cs` (new) — overlay phase; passes a live `() => _ctx.Lockstep.LocalFaction` getter (review patch).
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + `ScenePhaseOrder.cs` + `MainScene.cs` — `CustomHud` handle, "CustomHudOverlay" phase inserted after Hud, pumped in `_Process`.
- `godot/src/CreationSuite/CustomUiBuilderPanel.cs` (new) — palette+inspector authoring builder with live 16:9 preview (built; not yet wired into an editor entry point — deferred).

**Files changed (tests):** new `Dsl/CustomUiGateTests`, `Definitions/CustomUiSerializationTests`, `Validation/CanonicalModelHashCustomUiTests`, `Sim/DslVarReadbackTests`, plus review-added `Validation/ScenarioValidatorCustomUiTests` and `Sim/ScenarioDirectorReadbackTests`; `Golden/hero-start-state.golden.txt` re-recorded (the one dependent golden); AlgoVersion pins bumped 8→9 across ~11 guard-test files; `Bootstrap/PhaseOrderTest` gains "CustomHudOverlay".

**Review findings breakdown:** 6 patches applied (per-player Neutral-slot correctness fix; tree-container fail-closed; 11 per-field handshake-fold sensitivity pins; gate adoption-site integration tests; read-rail end-to-end ticking test; stale-comment fixes) — 0 high, 3 medium, 3 low. 3 deferred (builder reachability/authoring completeness; `WidgetFormat` unit coverage; canvas/geometry gate hardening) — logged in `deferred-work.md`. 9 rejected (intent mandates the handshake fold + re-baseline; recursion bounded by STJ MaxDepth + gate; tearing findings are false-criticals under single-threaded `_Process`; assorted convention-consistent notes). 0 intent_gap, 0 bad_spec — no loopback.

**Verification performed:**
- `dotnet build godot/godot.sln` → Build succeeded, 0 warnings / 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests` → 2314 passed, 1 skipped (pre-existing reserved test), 0 failed. (The `SpawnUnit_AllocatesZeroBytes_AfterWarmup` allocation-measurement test flaked once under the patch subagent then passed on independent re-run; it touches none of the changed code.)
- `git diff --stat -- .../Golden/` → only `hero-start-state.golden.txt` moved among `*.golden.txt`; 24 world replay goldens byte-identical.
- `AlgoVersion` = 9; `SimChecksum.AlgorithmVersion` = 17; sim/definitions files Godot-free/float-free; no write-rail scope — all confirmed by grep.
- Matrix Test Audit: every I/O-matrix row covered by a test that ran and passed (readback exclusion from `SimChecksum` proven structurally + by the unchanged replay goldens); presentation-rendering + mm:ss tails fall to the manual in-engine check per the project's presentation-exempt-from-Tier-1 convention.

**Residual risks:** (1) The authoring `CustomUiBuilderPanel` is not surfaced in-engine, so AC #2's builder path is currently JSON-only (deferred). (2) `WidgetFormat` (presentation-only mm:ss/format logic) is unit-untested under the current Tier-1 harness (deferred). (3) Per-player bind correctness and the 16:9 live-render behavior are verified by inspection/build rather than automated tests (the bridge is a Godot type) — an in-engine godot-verify pass would confirm them; `followup_review_recommended: true` because the F1 fix changed runtime behavior + the `Initialize` signature without its own adversarial pass.

### Follow-up review (2026-07-17)

The recommended follow-up review ran a fresh independent four-layer pass and **caught that the prior pass's own F1 patch was inverted** — per-player `{variable}` binds were reading the next player's slot (a HIGH-severity correctness regression on the read rail's marquee use case: scoreboards). Root-caused from first principles against four `ScenarioDirector` call sites: the DSL per-player store is 0-based with **slot 0 = Player1**, but the phase getter passed the 1-based engine faction (`Player1 = 1`) as the slot. Fixed via a new tested `DslVarReadback.PlayerSlotForFaction` conversion (`engineFaction - 1`, Neutral→0) wired into `CustomHudOverlayPhase`, plus two Tier-1 regression tests pinning the mapping at the readback seam.

Also applied 5 low patches (nested-child double-offset in `CustomUiBridge`; Fixed-precision loss in `WidgetFormat.Fraction`; a missing `ChangedId_MovesHash` v9 fold pin; and two doc-accuracy corrections — the double-buffer thread-safety overclaim and the `WidgetFormat` "consolidation" claim). Logged 2 new deferred entries (repeater `rows`/`max` lower-bound + hash-vs-render divergence; parse-before-cap breadth). Rejected 6 (including confirming `CheckArrayBind`'s "missing scalar check" is unreachable — the declaration gate already rejects non-scalar array elements — and that `WidgetFormat` genuinely cannot join the float-free Tier-1 source set, so the prior "untested" defer stands).

**Verification (follow-up):**
- `dotnet build godot/godot.sln` → Build succeeded, 0 errors; no new warnings attributable to the touched files.
- `dotnet test godot/ProjectChimera.Sim.Tests` → **2317 passed, 1 skipped, 0 failed** (+3 new tests over the prior 2314). The new `ChangedId_MovesHash` also confirms `Id` is folded.
- Unchanged: `CanonicalModelHash.AlgoVersion` = 9, `SimChecksum.AlgorithmVersion` = 17, all world goldens untouched (no hash/checksum/golden code was touched this pass); `DslVarReadback` remains Godot-free/float-free (the new helper is int-only).

**Files changed (follow-up patches):** `DslVarReadback.cs` (new `PlayerSlotForFaction` helper + double-buffer doc reword), `CustomHudOverlayPhase.cs` (faction→slot conversion + corrected comment), `CustomUiBridge.cs` (nested-child `Vector2.Zero` origin + `Initialize` doc), `WidgetFormat.cs` (Fixed-precision `Fraction` + doc reword); tests `DslVarReadbackTests.cs` (+2) and `CanonicalModelHashCustomUiTests.cs` (+1).

`followup_review_recommended` remains **true**: this pass changed runtime behavior on the primary per-player path and added a sim-side API (`PlayerSlotForFaction`); although now covered by two regression tests, the behavior change on a critical path — and the fact that the previous "fix" here was wrong — warrants one more independent confirmation, ideally an in-engine godot-verify of a per-player scoreboard for both a local Player1 and Player2.
