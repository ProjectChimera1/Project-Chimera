---
title: 'Story 3.6: Archetype + orthogonal ability/behavior composition (no subclassing)'
type: 'feature'
created: '2026-07-07'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '11e4ac8cd847d3e5d78ebb1f2b936094a4d1b10a'
final_revision: 'c17b5ead78a1b3f9ea99213495363d1e2823c4d4'
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** A creator cannot compose a unit's non-stat identity in the Unit Card Editor. Story 3.4 shipped the archetype dropdown (`category`) and *validates* ability references, but the `abilities` list is **read-only** in the form (editable only via the raw-JSON hatch — `AddAbilitiesRow`, `UnitCardPanel.Edit.cs:378`), and there is **no "behavior" concept anywhere** in the codebase. So the platform's core promise — a "healer" is `ranged archetype + heal ability + support behavior`, built by composition, never a subclass — is not yet authorable.

**Approach:** Add the **authoring data model + validation only** for orthogonal composition (running abilities/behaviors is later-epic combat work). (1) A net-new **data-driven `BehaviorDefinition` + `BehaviorRegistry`** mirroring `AbilityDefinition`/`AbilityRegistry`, loaded from `resources/data/behaviors/`, each behavior declaring its own `compatible_archetypes`. (2) A new additive `behaviors: string[]` field on `UnitDefinition` (mirrors `abilities`). (3) Extend `UnitDefinitionValidator` to reject undefined behavior refs and archetype-incompatible behaviors (undefined-ability + invalid-archetype already exist). (4) In-editor **structured pickers** (advanced mode) for abilities and behaviors that replace the read-only row, plus **simple-mode preset ability bundles**, all routed through the existing set→commit→undo→validate→save pipeline and the raw-JSON round-trip.

## Boundaries & Constraints

**Always:**
- **Composition is purely additive data on `UnitDefinition`** — one `category` archetype (unchanged, the closed set of 6) + zero-or-more `abilities` ids + zero-or-more `behaviors` ids. No per-unit subclass, no 7th `UnitCategory` (heroes precedent, `UnitDefinition.cs:159-173`).
- **Behaviors are data-driven definitions** (`BehaviorDefinition` deserialized from JSON via `BehaviorRegistry.LoadFromDirectory`, the single content-load choke point) — never a hardcoded closed enum and never a hardcoded compat matrix. Each behavior owns its `compatible_archetypes`; an omitted/empty list = compatible with all archetypes (permissive default, so shipping a behavior can't retro-break a unit).
- **Reuse the existing pipeline, do not rebuild it.** Extend `UnitCardPanel`/`UnitCardPanel.Edit.cs` in place: `AddSelect`/`MakeBadge`/`ShowBadge`/`PushHistory`/`CommitStr`/`OnLiveChanged`/`RevalidateAndReflect`, the `_segment` Simple/Advanced disclosure, the `_jsonPane` raw hatch, and `FactionWriter.PatchFactionJson` persistence. Mirror the `abilities`↔`AbilityRegistry` and `AbilityPresets` (Story 2.5a) patterns for the behavior/preset analogues.
- **Every new control carries a hover-AND-keyboard-focus tooltip** (`AttachFieldTip` sets `MouseFilter=Stop` + `FocusMode=All`, `Edit.cs:173`), per UX-DR53/NFR-2.
- **Pure authoring-time, zero determinism fold.** The `behaviors` field is unread by the sim (like `abilities` as a string is): no `EntityWorld`/SoA/store touch, no checksum/golden change, no new scene phase. `CanonicalModelHash` references factions by path + unit-id string, never by unit fields (3.4 posture) — stamps stay **9/3/1/2 + StartStateHash 1**; all **18 goldens byte-identical**; `PhaseOrderTest` untouched. New `src/Core/Definitions` files are Godot-free and analyzer-clean.

**Block If:**
- The intent turns out to require a **behavior runtime** (any sim system that reads/executes a behavior — utility-AI stance switching, auto-cast, target selection). That is later-epic combat/AI work — HALT with status `blocked` and that as the blocking condition rather than wiring a sim consumer.
- The compat model needs to reject a *combination* beyond "this behavior lists archetypes and this unit's archetype is not among them" (e.g. ability↔behavior cross-constraints, mutually-exclusive behaviors). Nothing in the intent defines such rules — HALT with `blocked` / `composition rule undefined` rather than inventing one.

**Never:**
- No behavior SoA array / no `EntityWorld` field / no `Resolve*` index back-fill for behaviors / no checksum fold (nothing consumes them yet). No `Fixed`/sim/determinism changes.
- No hero promotion (`is_hero` stays read-only — Story 3.7), no attack-delivery flag (3.12), no editing Buildings or the beta faction, no `user://` authoring home. No second editor panel.
- Do not change how the existing `category` archetype dropdown or the existing undefined-ability validation work — extend, don't rewrite.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Attach ability (advanced picker) | Card open in Advanced; pick an ability from the "Add ability" list | `def.Abilities` gains the id (a chip appears); change is undoable (Ctrl+Z); Save persists `abilities` | No error expected |
| Attach compatible behavior | Behavior whose `compatible_archetypes` includes `def.Category` | `def.Behaviors` gains the id (chip appears); state stays Valid; Save persists `behaviors` | No error expected |
| Attach archetype-incompatible behavior | e.g. a mobility behavior (Structure not in its list) on a `Structure` unit | Located UX-DR55 badge on the **behaviors** field; "N field(s) need attention"; Save/Playtest blocked | Badge, not a crash |
| Undefined behavior ref | `behaviors` contains an id absent from `BehaviorRegistry` (typed via raw JSON) | Located badge on **behaviors**; Save blocked | Badge |
| Undefined ability ref | `abilities` contains an unknown id (raw JSON) | Located badge on **abilities** (existing 3.4 rule, still fires through the picker path) | Badge |
| Remove a component | Click a chip's ✕ on an attached ability/behavior | id removed from the array; undoable; empty array persists (cleared) | No error expected |
| Simple preset bundle | Simple mode; pick a preset ability bundle | `def.Abilities` is set to the bundle's ids (unknown-in-registry ids dropped); switching to Advanced shows those as chips; raw JSON reflects them | No error expected |
| Raw-JSON round-trip | A valid composed unit serialized then re-parsed via the advanced raw pane | Identical `UnitDefinition` (archetype + abilities + behaviors) — no field lost or added | No error expected |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `behaviors` field (JSON `behaviors`, `string[]` = empty) mirroring `Abilities` (`:126-127`); no `Resolve`/SoA — authoring data only. `Category`/`ParsedCategory` (`:301-309`) is the archetype (unchanged).
- `godot/src/Core/Definitions/AbilityDefinition.cs` + `AbilityRegistry.cs` -- **templates** for the new behavior types (`AbilityRegistry`: `LoadFromDirectory` :71, `IndexOf` :56, `All` :31, `Get` :50, `Empty` :35, `Count` :28).
- `godot/src/Core/Definitions/AbilityPresets.cs` -- **template** for `UnitCompositionPresets` (closed `enum Kind` :28, `All` label table :60, pure `Build` :108, matcher precedent `AbilityPresetMatcher.cs`).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- `Validate(def, registry, siblings)` :72; `InSet` enum rule (`_categories` :60, archetype check :101); undefined-ability rule (`registry.IndexOf(aid) < 0`, keyed `abilities`) :137-148; multi-error `UnitValidationResult` :17. **Extend** with a `BehaviorRegistry?` param + behavior rules.
- `godot/src/Core/Definitions/FactionWriter.cs` -- `ApplyFields` :188 writes unit fields; `PutStringArray(obj, "abilities", d.Abilities, defaultsNull:false)` :219 (mirror for `behaviors`). Surgical DOM patch preserves untouched tokens.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- `AddAbilitiesRow` :378 (read-only, **replace** with picker); `AddSelect` enum/dropdown builder :329; `AddFieldRow`/`MakeBadge`/`ShowBadge` :151/:165/:487; `PushHistory` :511; `OnLiveChanged` :404; `RevalidateAndReflect` :467 (calls `_validator.Validate` — update args); `_segment` Simple/Advanced :OnSegmentChanged :144; `_advancedHost` :108; raw pane `SaveFromRawPane` :676; `Categories` :30.
- `godot/src/CreationSuite/UnitCardPanel.cs` -- `_registry` (AbilityRegistry) :46; `Initialize(faction, gameState, registry, factionJsonPath)` :100. **Add** `_behaviorRegistry` + Initialize param.
- `godot/src/Core/MainScene.cs` -- `ABILITIES_DIR` const :186; `AbilityRegistry.LoadFromDirectory(abilitiesAbs)` :264; SceneContext build `AbilityRegistry = _abilityRegistry` :334. **Add** the behaviors analogue.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- `AbilityRegistry` field :58 (add `BehaviorRegistry`).
- `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs` -- `Initialize(... _ctx.AbilityRegistry ...)` :36 (thread `_ctx.BehaviorRegistry`).
- `godot/src/UI/Components/ChimeraComponents.*.cs` -- `Select`, `Tag`, `IconButton`, `Input` for the picker chips + "Add" control.
- `godot/ProjectChimera.Sim.Tests/Definitions/` -- Tier-1 home (e.g. `UnitDefinitionValidatorTests`, `AbilityPresetTests`) for the new Godot-free tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `[JsonPropertyName("behaviors")] public string[] Behaviors { get; set; } = System.Array.Empty<string>();` with a doc comment mirroring `Abilities` (authoring data, resolved/consumed by no sim system yet — validation only). No `Parsed*`/`Resolve`/`[JsonIgnore]` index members.
- `godot/src/Core/Definitions/BehaviorDefinition.cs` -- NEW Godot-free POCO: `id` (JSON `id`, stable ref id), `display_name`, `description`, `compatible_archetypes` (`string[]?`, JSON `compatible_archetypes`; null/empty ⇒ all). Add a lenient `IsCompatibleWith(string category)` (true when the list is null/empty or contains `category`, case-sensitive to match `UnitCategory` strings).
- `godot/src/Core/Definitions/BehaviorRegistry.cs` -- NEW, a structural clone of `AbilityRegistry`: `Empty`, ctor over `IReadOnlyList<BehaviorDefinition>`, `Count`/`All`/`Get(int)`/`IndexOf(string)`, and `LoadFromDirectory(absDir, onSkipped)` (one JSON per file, deterministic ordering, drop files that fail a minimal validity check — non-empty id, valid archetype tokens). Reject/skip a behavior whose `compatible_archetypes` contains a token outside the 6-archetype set.
- `godot/resources/data/behaviors/*.json` -- NEW seed set so the feature is demonstrable and the ACs' cases exist: at least `support.json` (compatible with all incl. Ranged → the healer example), and one archetype-restricted behavior (e.g. `skirmish.json` with `compatible_archetypes` excluding `Structure`) to make the incompatible-composition case reachable. Small, plainly-authored files.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- add an optional `BehaviorRegistry? behaviorRegistry = null` param to `Validate(...)` (optional keeps existing callers/tests compiling). New rules, appended to the error list (multi-error, not first-fail): each `def.Behaviors[i]` with `behaviorRegistry.IndexOf(id) < 0` → located error keyed `behaviors`, path `behaviors[{i}]` ("undefined behavior"); each resolvable behavior whose `IsCompatibleWith(def.Category)` is false → located error keyed `behaviors` ("behavior '{id}' is not compatible with the {category} archetype"). Skip both when `behaviorRegistry` is null (mirrors the ability-null guard).
- `godot/src/Core/Definitions/FactionWriter.cs` -- in `ApplyFields`, add `PutStringArray(obj, "behaviors", d.Behaviors, defaultsNull: false);` immediately after the `abilities` line (:219). Empty-and-unchanged stays absent → existing faction JSON untouched.
- `godot/src/Core/Definitions/UnitCompositionPresets.cs` -- NEW Godot-free, mirroring `AbilityPresets`: a closed `enum Kind` of a few role bundles (e.g. `Custom`, `Healer`, `Bruiser`, `Caster`), an `All` label table driving the Simple-mode Select, `Bundle(Kind) → string[] abilityIds`, and `Detect(string[] abilities) → Kind` (id-set equality; no match ⇒ `Custom`) for lossless round-trip. Bundles reference ability ids that ship under `resources/data/abilities/`; applying a preset drops ids absent from the live `AbilityRegistry` (same lenient posture as `ResolveAbilities`).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- (a) replace `AddAbilitiesRow` with `AddComponentPicker(parent, "Abilities", "abilities", () => def.Abilities, v => def.Abilities = v, _registry ids/labels, def)`: renders attached ids as chips (`Tag` + an ✕ `IconButton` that removes), plus an "Add" `Select` listing registry items not yet attached; each add/remove writes the `string[]`, `PushHistory`, `OnLiveChanged("abilities")`; keeps `MakeBadge("abilities")` on the row. (b) an identical `AddComponentPicker` for behaviors over `_behaviorRegistry`, keyed `behaviors`, in the Advanced subtree. (c) Simple-mode preset: an `AddSelect`-style "Composition" dropdown built from `UnitCompositionPresets.All`, preselected via `Detect(def.Abilities)`; on select, apply `Bundle(kind)` (registry-filtered) to `def.Abilities`, `PushHistory`, `OnLiveChanged`, `Refresh()`. Route the picker `Validate` call and everything through the existing commit/undo/validate path; attach tooltips (`AttachFieldTip`) to every new control.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` (`RevalidateAndReflect`, :467) -- pass `_behaviorRegistry` into `_validator.Validate(_current, _registry, _behaviorRegistry, _faction?.Units)`; behavior errors map to the `behaviors` badge via the existing `ShowBadge` loop (keys already match).
- `godot/src/CreationSuite/UnitCardPanel.cs` -- add `private BehaviorRegistry _behaviorRegistry = BehaviorRegistry.Empty;`; extend `Initialize(..., BehaviorRegistry behaviorRegistry, ...)` (or add after `registry`) storing it; default `Empty` on null.
- `godot/src/Core/MainScene.cs` -- add `BEHAVIORS_DIR = "res://resources/data/behaviors/"`; load `_behaviorRegistry = BehaviorRegistry.LoadFromDirectory(GlobalizePath(BEHAVIORS_DIR), ...)` beside the ability registry (~:264); set `BehaviorRegistry = _behaviorRegistry` in the SceneContext build (~:334). No `Resolve` loop (behaviors aren't resolved).
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- add `public BehaviorRegistry BehaviorRegistry = BehaviorRegistry.Empty;` (:58 neighbourhood).
- `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs` -- pass `_ctx.BehaviorRegistry` into `_ctx.UnitCardPanel.Initialize(...)` (:36).
- `godot/ProjectChimera.Sim.Tests/Definitions/BehaviorAndCompositionTests.cs` -- NEW Tier-1 tests (Godot-free) covering: `BehaviorRegistry.LoadFromDirectory` (valid load, skip invalid, deterministic order, unknown-archetype token rejected); `BehaviorDefinition.IsCompatibleWith` (null/empty ⇒ all; listed ⇒ only those); validator rules (undefined behavior ref located error; archetype-incompatible located error; valid composition ⇒ 0 errors; null registry ⇒ behaviors unchecked); `UnitCompositionPresets` `Bundle`/`Detect` round-trip (each bundle detects back to its Kind; arbitrary set ⇒ `Custom`); and `FactionWriter` behaviors round-trip (patch adds `behaviors`, empty stays absent, other tokens byte-preserved).

**Acceptance Criteria:**
- Given the Unit Card Editor open on a unit, when I pick one of the 6 archetypes and attach zero-or-more abilities and behaviors (a healer = Ranged + a heal ability + `support`), then the composition is stored as purely additive `category`/`abilities`/`behaviors` data on the `UnitDefinition` (no subclass), Save writes the `behaviors` array into the faction JSON, and reload reproduces the same composition.
- Given a validate-before-save (AR-39), when the unit references an undefined ability, an undefined behavior, an invalid archetype, or an archetype-incompatible behavior, then each is rejected with a located UX-DR55 badge on the offending field and Save/Playtest is blocked; and a fully-valid composition saves with no badges and round-trips through the advanced raw-JSON view unchanged.
- Given the Simple/Advanced disclosure (UX-DR54), when I compose in Simple mode I use preset ability bundles, and in Advanced mode I get the individual ability picker, the behavior picker, every component field, and the raw-JSON escape hatch (FR-6); every new control shows a tooltip on hover and on keyboard focus (UX-DR53).
- Given this is authoring-time presentation + Godot-free definition work, when the build and Tier-1 suite run, then `godot.csproj` compiles 0-error, all Tier-1 tests pass (including the new behavior/composition/preset/writer tests), the 18 goldens are byte-identical, the sim stamps (9/3/1/2 + StartStateHash 1) are unchanged, `PhaseOrderTest` is untouched, and the release analyzer gate holds (RS0030 zero-baseline).

## Design Notes

- **D-1 — Behaviors are data-driven definitions, not a hardcoded enum.** The epics Technical Decision + story note both direct "new C# definition classes in `src/Core/Definitions` deserialized from JSON," and the platform rule forbids a gameplay rule (the archetype-compat matrix) hardcoded where a creator can't reach it. So `BehaviorDefinition` owns `compatible_archetypes` as data and `BehaviorRegistry` mirrors `AbilityRegistry` exactly. This makes the AC2 "archetype-incompatible component" check a data lookup (`behavior.IsCompatibleWith(unit.category)`), not a C# matrix.
- **D-2 — No behavior runtime, no fold (deliberate).** Unlike `abilities` (which Epic 2 resolves to SoA indices and casts), `behaviors` gets **no** `Resolve*`, no `EntityWorld` array, no checksum fold — nothing consumes it at runtime this story ("authoring data model + validation only"). Adding an unread `string[]` to `UnitDefinition` moves no golden (`CanonicalModelHash` references by path + id string, 3.4-verified) and keeps stamps at 9/3/1/2 + StartStateHash 1. If a future story (utility-AI) needs a runtime, it adds the resolve+fold then — this story reserves only the authored field.
- **D-3 — Structured picker shape (reuse, don't invent a new primitive).** Compose the picker from existing kit parts: attached ids as `Tag` chips each with an ✕ `IconButton`, plus an "Add …" `Select` of not-yet-attached registry entries (id + `display_name`). It writes the same `string[]` the raw-JSON pane and `FactionWriter` already handle, and reuses `PushHistory`/`OnLiveChanged`/`RevalidateAndReflect`, so undo, live badges, and Save need no new plumbing. Advanced holds the granular pickers ("every component field"); Simple holds the preset-bundle Select.
- **D-4 — Optional validator param = zero-break extension.** `BehaviorRegistry? behaviorRegistry = null` as a new optional arg means existing `Validate` callers/tests compile unchanged and behavior rules simply don't run when unsupplied; the editor is the one caller that passes it. Behavior errors reuse the `behaviors` badge key already present on the row.
- **Example — behavior JSON + compat:**
  ```json
  { "id": "support", "display_name": "Support",
    "description": "Prioritizes aiding and protecting allied units.",
    "compatible_archetypes": ["Worker","Melee","Ranged","Siege","Air","Structure"] }
  ```
  `skirmish.json` omits `Structure` from its list → attaching `skirmish` to a `Structure` unit yields the AC2 located `behaviors` badge; attaching `support` to a `Ranged` healer validates clean.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors (3 pre-existing CS86xx warnings only).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass incl. the new `BehaviorAndCompositionTests`; 18 faction goldens byte-identical; sim stamps 9/3/1/2 + StartStateHash 1 unchanged. (The pre-existing WSL-only `ProceduralMapGeneratorTests.SameSeed_…` golden-env mismatch is unrelated and identical on a clean baseline.)

**Manual checks (in-engine via `/godot-verify`):**
- Enter Edit mode, press `J` to open the Unit Card Editor on a Ranged unit. Advanced → "Add ability" a heal ability (chip appears; state Valid); "Add behavior" `support` (chip appears; Valid); Save → the faction JSON gains `abilities`/`behaviors` arrays. Add `skirmish` to a Structure unit → located `behaviors` badge, Save disabled. Simple → pick the "Healer" preset bundle → abilities populate; switch to Advanced → those show as chips; Raw JSON reflects them and re-parses identically. Ctrl+Z reverts an attach. Every new control shows a tooltip on hover and on keyboard focus.

## Review Triage Log

### 2026-07-07 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 3, low 1)
- defer: 2: (high 0, medium 1, low 1)
- reject: 11
- addressed_findings:
  - `[medium]` `[patch]` Null-guarded the `Prerequisites`/`Abilities`/`Behaviors` clone in `CloneUnit` (`UnitCardPanel.Edit.cs`): a raw-JSON `"behaviors": null` no longer NullReference-crashes the Duplicate action.
  - `[medium]` `[patch]` Preset apply now early-returns when the registry-filtered bundle is empty (`AddCompositionRow`): a role preset whose ability ids are all absent from the live registry no longer silently wipes the unit's authored ability set.
  - `[medium]` `[patch]` Chip render uses a single null-safe catalog lookup (`AddComponentPicker`): a null/unknown attached id (from a hand-authored raw-JSON array) renders as the raw id (still validator-badged) instead of throwing `InvalidOperationException`; also removed the redundant double-enumeration.
  - `[low]` `[patch]` Wired the collected behavior `Desc` into the remove-chip tooltip (was dead data the doc comment claimed fed the tooltip); added a Tier-1 test asserting a null `behaviors` element is rejected fail-closed rather than crashing.

## Auto Run Result

Status: done

### Summary
Implemented Story 3.6 — orthogonal archetype + ability/behavior composition, **authoring data model + validation only** (no behavior runtime, no determinism fold). Archetype (`category`) and the `abilities` ref-list already existed (3.4/Epic 2); this story adds the net-new **behavior** axis and promotes both axes to structured in-editor composition:
- New data-driven `BehaviorDefinition` + `BehaviorRegistry` (mirror of `AbilityDefinition`/`AbilityRegistry`), loaded from `resources/data/behaviors/`; each behavior declares its own `compatible_archetypes` (empty ⇒ all) so archetype-compat is data, not a hardcoded matrix.
- New additive `behaviors: string[]` field on `UnitDefinition` (unread by the sim ⇒ no SoA/checksum/golden touch).
- `UnitDefinitionValidator` gains a `BehaviorRegistry?` overload rejecting undefined behavior refs and archetype-incompatible behaviors (undefined-ability + invalid-archetype already existed).
- Unit Card Editor: read-only abilities row replaced by a structured chips+Add picker for both abilities and behaviors; Simple-mode "Composition" preset-ability-bundle dropdown; all routed through the existing set→undo→validate→save→raw-JSON pipeline.

### Files changed
- `godot/src/Core/Definitions/BehaviorDefinition.cs` (new) — POCO + lenient `IsCompatibleWith`.
- `godot/src/Core/Definitions/BehaviorRegistry.cs` (new) — `AbilityRegistry` clone; skips empty-id / unknown-archetype-token files.
- `godot/src/Core/Definitions/UnitCompositionPresets.cs` (new) — closed `Kind` (Custom/Healer/Bruiser/Caster) + `Bundle`/`Detect`.
- `godot/resources/data/behaviors/support.json`, `skirmish.json` (new) — seed set (support = all archetypes; skirmish excludes Structure).
- `godot/src/Core/Definitions/UnitDefinition.cs` — additive `Behaviors` field.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — `BehaviorRegistry?` overload + two behavior rules.
- `godot/src/Core/Definitions/FactionWriter.cs` — persist `behaviors`; `arr.Add((JsonNode)s)` fix (see below).
- `godot/src/CreationSuite/UnitCardPanel.cs` / `UnitCardPanel.Edit.cs` — `_behaviorRegistry`, structured pickers, preset dropdown, validator wiring, clone; review patches (null-safe clone/chip, preset no-wipe, Desc tooltip).
- `godot/src/Core/MainScene.cs`, `Bootstrap/Phases/SceneContext.cs`, `Bootstrap/Phases/UnitCardPhase.cs` — load + thread `BehaviorRegistry`.
- `godot/ProjectChimera.Sim.Tests/Definitions/BehaviorAndCompositionTests.cs` (new) — 17 Tier-1 tests.

### Beyond-spec change
`FactionWriter.PutStringArray`: `arr.Add(s)` → `arr.Add((JsonNode)s)`. The generic `JsonArray.Add<string>` mints a `JsonValueCustomized<string>` that the resolver-less `IndentedOptions` cannot serialize; the behaviors fresh-write path (AC1) is the first test to exercise it. The verification-gap reviewer confirmed this is verified (the two behaviors round-trip tests fail on revert) and protects the shared abilities/prerequisites/tags path transitively.

### Review findings breakdown
- **4 patches applied** (see Review Triage Log): null-safe clone, preset no-wipe guard, null-safe chip render, Desc tooltip + null-element test.
- **2 deferred** (to `deferred-work.md`): (a) the 6-archetype closed set is duplicated across `UnitDefinitionValidator._categories`, `BehaviorRegistry._archetypes`, and a validator error string (drift risk when a 7th archetype lands); (b) the in-editor composition UI (pickers/preset/undo wiring) has no automated verification — Godot-`Control` code isn't Tier-1-loadable, verified only by live in-engine driving; recommend extracting the array-mutation/undo logic to a Godot-free seam or a scripted godot-mcp check of record.
- **11 rejected**: preset composing abilities-only (AC3 says "preset ability bundles" — literal; the healer is fully authorable in Advanced), Detect ability-only labeling, cascaded validator noise on an already-invalid archetype, empty-category message spacing, malformed-token drops-whole-behavior (by-design fail-closed, logged), duplicate/unsanitised behavior ids (mirrors `AbilityRegistry`), duplicate behavior ref, null-array-element asymmetric round-trip in the shared `PutStringArray`, boot-time behavior load in play builds, server/scenario not validating behaviors (by design, D-2), and "FactionWriter fix untested for other fields" (confirmed not-a-gap).

### Verification
- `dotnet build godot/godot.csproj` → Build succeeded, 0 errors (3 pre-existing CS8632 warnings).
- `dotnet test godot/ProjectChimera.Sim.Tests` → 787 passed / 1 skipped / 1 failed; the sole failure `ProceduralMapGeneratorTests.SameSeed_…` is confirmed pre-existing on the clean baseline `11e4ac8` (stash-and-retest: still fails identically) — a WSL/Linux map-gen golden-env mismatch unrelated to this change. 17/17 `BehaviorAndCompositionTests` pass. Faction goldens + sim stamps (9/3/1/2 + StartStateHash 1) unchanged; `PhaseOrderTest` untouched.
- **In-engine (`godot-mcp`, live-driven):** opened the Unit Card Editor in Edit mode; Advanced view built the ability picker (8 registry abilities), behavior picker (`support`/`skirmish`), composition preset dropdown (Custom/Healer/Bruiser/Caster), and attached-component chips. Attaching Structure-incompatible `skirmish` flipped the panel to "1 field(s) need attention" + disabled Save (AC2); switching the same behavior's unit to Ranged re-enabled Save (archetype-sensitive compat, live). Post-patch smoke: panel rebuilds and the Bruiser preset applies with zero runtime errors. No faction JSON was persisted (in-memory drive only) — working tree carries only the intended source changes.

### Residual risks
- The in-editor composition UI (pickers, preset dropdown, undo closures) has no headless test (Godot-`Control` constraint) — covered by live in-engine driving this pass; see the deferred item recommending a testable seam.
- `behaviors` is authoring-only with no sim consumer this epic (by design); a future runtime story must add resolution + any determinism fold.
