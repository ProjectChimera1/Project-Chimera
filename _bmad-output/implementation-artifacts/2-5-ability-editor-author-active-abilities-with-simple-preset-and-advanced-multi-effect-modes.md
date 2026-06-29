# Story 2.5: Ability Editor — author active abilities with simple-preset and advanced multi-effect modes

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a **creator**,
I want **an in-app Ability Editor to author an active ability (targeting, cost, cooldown, ≥1 effect) using either configurable presets (simple mode) or full multi-effect composition with a raw-JSON escape hatch (advanced mode)**,
so that **I can build a new active ability without writing code — simply or deeply — and have it validated before it can break a match**.

This is the first **authoring surface** for the D1 effect engine: Stories 2.1 (effect vocabulary), 2.2 (modifiers), 2.3 (the `AbilityDefinition` model + `Validated<T>` gate + JSON converter), and 2.4a/b (runtime cast spine + command-card UI) built the *consumption* path. Story 2.5 builds the *production* path. It emits the **same `AbilityDefinition` JSON** that 2.3 loads and 2.4 attaches — **no new runtime path, no new sim state.**

## Acceptance Criteria

**AC1 — Simple-preset mode (epic AC1; FR-8, FR-10).**
**Given** the Ability Editor in simple mode **When** the creator picks the `'targeted damage'` preset, sets a damage value and cooldown, and saves **Then** a valid `AbilityDefinition` is produced, passes `AbilityValidator`, is written to `resources/data/abilities/<id>.json`, and is **attachable to a unit (2.4) and castable in a match** **And** the creator never edited JSON or wrote code to reach this result.
- *"Castable in a match" is satisfied by the established **save → scene-reload** mechanism (see Decision #2): after the new file is written, the next match/scenario load rebuilds `AbilityRegistry` from the directory and the ability resolves and casts. There is no hot-reload into a running match, and the editor does **not** build one.*

**AC2 — Advanced multi-effect mode + raw-JSON round-trip (epic AC2; FR-10, UX-DR54).**
**Given** the Ability Editor in advanced mode **When** the creator composes a multi-effect graph (e.g. `DirectHpDelta` self-cost + `SearchArea` → `Heal` allies, wrapped in a `Sequence`) and saves **Then** the composed multi-primitive ability validates and executes as one `Sequence` on cast **And** a **raw-JSON view** of the same ability is available and **round-trips**: editing the JSON and reparsing yields an identical graph, and switching back to the structured view reflects the edit (per UX-DR54).
- *Round-trip identity is judged on the **parsed graph's `Fixed.Raw` values + node structure**, not on text equality (see Decision #1 / the `FixedJsonConverter` note).*

**AC3 — Validate-gated save with inline located errors (epic AC3; AR-13, NFR-6).**
**Given** an ability authored with an invalid configuration (e.g. composition depth over cap, a `Sequence` with 0 children, an unknown/float value via the raw-JSON pane) **When** the creator attempts to save **Then** the save is **blocked**, and the editor shows the validator's **located error string naming the offending node/field** (e.g. `ability 'x'.effect.children[2]: SequenceEffect has 0 children…`) inline **And** **no invalid ability is ever written** to a loadable definition file (the file is written only on a passing `Validated<AbilityDefinition>`).

**AC4 — Determinism fence (story-added; mirrors 2.4b AC5).**
**Given** this is a pure authoring/presentation story that writes load-time JSON and adds no per-entity SoA, no tick system, and no mutable mid-match state **When** the change is complete **Then** `SimChecksum.AlgoVersion` is still **7**, `SimChecksum.cs` and `SystemOrderTest.cs` are **untouched**, **no new golden** is added, and **all 9 existing goldens are byte-identical** (a moved golden = a leaked sim write → fix it, do **not** re-record). Completing `EffectNodeJsonConverter.Write` is authoring-only and is never invoked during a tick or checksum.

**AC5 — Closed vocabulary, house-style surface (story-added; AR-13, NFR-6, UX-DR33/DR54/DR70).**
**Given** the editor is a creator-facing surface on the D1 engine **When** it is used **Then** it offers **only the closed effect vocabulary** — no scripting, no free-text logic, no `customParams` — and it does **not** expose reserved/unimplemented choices: `DamageType.COUNT`, and `TargetFilter.Air/Ground/Structure` (reserved for Story 2.9a, hard-rejected by the converter) are **not** offered. **And** the panel follows the `TriggerEditorPanel` pattern (right-dock `CanvasLayer` panel, Edit-mode only), with a **Simple/Advanced segment toggle** (Simple default), composed from the existing ad-hoc UI conventions (Decision #4 / UX-DR33 note).

**AC6 — Godot-free authoring core, Tier-1 covered (story-added; testability).**
**Given** the serialize/validate/preset logic must be deterministic and testable **When** the story is implemented **Then** that logic lives in **Godot-free `src/Core/Definitions`** (the completed converter `Write`, an `AbilityPresets` builder) and is covered by **Tier-1 xUnit** tests: (a) round-trip `Fixed.Raw`-equality (serialize → `AbilityLoader.Load` → identical graph) including the 3 shipped samples; (b) each simple-mode preset expands to a graph that **passes** `AbilityValidator`; (c) an invalid graph yields a **located `AbilityValidationResult.Fail`** carrying no runnable `Validated<T>`. The thin Godot `AbilityEditorPanel` Control is verified via **`/godot-verify`** (no GdUnit4 harness exists).

## Tasks / Subtasks

> **Build order:** Task 1 (Godot-free core) first — it is Tier-1-provable before any Godot UI exists and de-risks the whole story. Then the panel (2), advanced composer (3), raw-JSON (4), save/validate (5), wiring (6), castable-verify (7), fence/verify (8).
>
> **⚠ Split line (see Decision #7):** Task 3 (the advanced *structured* multi-effect tree builder) is the natural 2.5b carve-off. Tasks 1–2, 4–8 deliver a shippable editor (simple presets + raw-JSON escape hatch, validated & castable). If Task 3 balloons, split via `gds-correct-course` — the raw-JSON pane (Task 4) already gives advanced users a multi-effect path without it.

- [ ] **Task 1 — Godot-free authoring core: serialize `Write` path + presets** (AC: 2, 3, 6)
  - [ ] 1.1 Complete `EffectNodeJsonConverter.Write` (`godot/src/Core/Definitions/EffectNodeJsonConverter.cs:63`) — replace the `throw new NotSupportedException(...)` with a full writer that emits each node's `kind` discriminator + that kind's fields for all 7 kinds (`direct_hp_delta`, `heal`, `damage`, `apply_modifier`, `sequence`, `search_area`, `persistent`) and the `modifier` sub-object, using the **same property names** the `Read`/`ReadModifier` path consumes. Recurse children via `JsonSerializer.Serialize(writer, child, options)` so nested `Fixed`/enums route through the registered converters. **Do not change `Read`.** This is authoring-only and is never called during a tick (AC4).
  - [ ] 1.2 Add `godot/src/Core/Definitions/AbilityPresets.cs` (Godot-free, `static`) — a **closed** set of simple-mode presets, each `(tuned params) → AbilityDefinition`. Minimum set mirroring the shipped samples + the epic's named presets: **Targeted Damage** (`targeting=TargetUnit`, `effect={damage, amount, damage_type=Magic}`), **Heal** (`targeting=Self`, `effect={heal, amount}`), **Self Buff** (`targeting=Self`, `effect={apply_modifier, modifier{attack_damage_delta, duration_ticks}}`), **AoE Nuke** (`targeting=TargetUnit`, `effect={search_area, radius, filter=Enemy, child={damage, damage_type=Magic}}` — i.e. fireball's proven shape: cast on an enemy unit, damage in a radius around it). Each carries `cost_energy`/`cost_ore`/`cost_crystal`/`cooldown` as tunable numerics. All numerics are `Fixed`/`int` — never `float` keyword in this file (analyzer CHM0001). **⚠ Every default preset targets `Self` or `TargetUnit` on purpose — `GroundPoint`'s *cast path* was deferred in Story 2.4 (the command card supports Self/TargetUnit only). A `GroundPoint` ability still **authors and validates** fine, but it would not be castable today, so presets must avoid it (keeps AC1's "castable in a match" true for every preset). Advanced/raw-JSON mode may still author `GroundPoint` — flag it in-UI as "authorable, cast support pending (2.4 deferral)."**
  - [ ] 1.3 Tier-1 tests in `godot/ProjectChimera.Sim.Tests/Definitions/` (new `AbilityRoundTripTests.cs`, `AbilityPresetTests.cs`): round-trip the 3 shipped sample files (`Load` → serialize via `ContentJson.Options` → `Load` again → assert `Fixed.Raw` + structure equal); each preset → `new AbilityValidator().Validate(...)` is `Ok`; serializing then reparsing every preset is graph-identical. Confirm `SimSources.props` already globs `src/Core/Definitions/**` (it does — no props edit; verify).

- [ ] **Task 2 — `AbilityEditorPanel` shell + Simple mode** (AC: 1, 5)
  - [ ] 2.1 New `godot/src/CreationSuite/AbilityEditorPanel.cs` — `public partial class AbilityEditorPanel : Node`, cloning the `TriggerEditorPanel` shell verbatim where possible: build a self-owned `CanvasLayer { Layer = 12 }` → right-anchored `PanelContainer` (`CenterRight`, ~440×600) → root `VBoxContainer`; lifecycle `Initialize(...)` / `Toggle()` / `_Ready()` → `BuildUi()` / `OnModeChanged(int)` (hide in Play mode). Match the house palette/StyleBox conventions copied from `SettingsPanel`/`TriggerEditorPanel` (dark navy bg, blue-grey border, accent-blue titles).
  - [ ] 2.2 Add the **Simple/Advanced segment toggle** at the panel top (UX-DR54/DR24) — a 2-button `ButtonGroup` pill row; **Simple is the default**; switching reveals/hides the advanced container + raw-JSON pane. Cross the `ContentBrowserPanel.SwitchTab`/`MakeTabButton` pattern (`godot/src/UI/ContentBrowserPanel.cs:197/214`) for the toggle behavior.
  - [ ] 2.3 **Common header fields** (both modes): `id` (sanitized to `[a-z0-9_]`, used as filename), `display_name` (text), `targeting` (a net-new styled `OptionButton` over the closed `{None, Self, TargetUnit, GroundPoint}` set — `AbilityTargeting` names). Net-new `OptionButton` widget — no wrapper exists; style inline.
  - [ ] 2.4 **Simple-mode body**: a preset `OptionButton` (the closed `AbilityPresets` set) + tuned numeric rows for that preset (damage/heal amount, cooldown, costs) using the `SettingsPanel.AddSliderRow`/`AddSectionHeader` pattern (`godot/src/UI/SettingsPanel.cs:230/221`) or net-new `SpinBox` rows. Changing the preset rebuilds the field set. The Simple model is an in-memory `AbilityDefinition` produced by the chosen `AbilityPresets` builder (AC1 "never edited JSON").

- [ ] **Task 3 — Advanced mode: structured effect-tree builder** *(splittable → 2.5b candidate)* (AC: 2)
  - [ ] 3.1 An effect-**tree** editor (the graph is a strict tree: `Sequence.children[]`, `SearchArea.child`, `Persistent.{initial,period,expire}` — **not** a free DAG, so a tree/list UI fits; Decision #4). Per node: a `kind` `OptionButton` over the closed 7, per-kind field editors (`delta`/`amount`/`damage_type`/`radius`/`filter`/`modifier{…}`), and add/remove/reorder/nest controls. Use the `TriggerEditorPanel.RefreshList` row pattern (`godot/src/CreationSuite/TriggerEditorPanel.cs:197`) for the node list.
  - [ ] 3.2 Enforce `EffectCaps` **in-UI** with friendly messaging *before* save: `MaxEffectDepth=8`, `MaxSequenceChildren=8` (and ≥1 — empty `Sequence` is invalid), `MaxSearchAreaDepth=2`, `MaxTotalEffectNodes=64`. Offer only authorable enum values (AC5: exclude `DamageType.COUNT`, `TargetFilter.Air/Ground/Structure`). The validator remains the source of truth on save (Task 5) — in-UI caps are a UX guardrail, not the gate.

- [ ] **Task 4 — Raw-JSON escape hatch + round-trip** (AC: 2)
  - [ ] 4.1 A multiline raw-JSON pane (`TextEdit`) revealed in Advanced mode. "Show JSON" serializes the **current in-memory `AbilityDefinition`** via `JsonSerializer.Serialize(def, ContentJson.Options)` (now possible after Task 1) — consider a local `WriteIndented` options clone for human readability (the `TriggerEditorPanel.cs:314-317` precedent), but parse/validate through the canonical `ContentJson.Options`/`AbilityLoader`.
  - [ ] 4.2 "Apply JSON" parses the edited text via `AbilityLoader.Load(text, "<editor>")` → on `Ok`, repopulate the structured model from the parsed `AbilityDefinition` (round-trip); on `Fail`, surface the located error (Task 5.3) and do not clobber the model. Round-trip identity asserted in Tier-1 on `Fixed.Raw` (Task 1.3).

- [ ] **Task 5 — Validate-gated Save** (AC: 1, 3)
  - [ ] 5.1 On **Save**: build the `AbilityDefinition` from the active mode's model → run `new AbilityValidator().Validate(def)` (or, when saving from the JSON pane, `AbilityLoader.Load`). Write the file **only** when the result `.Ok` is true (AC3: no invalid file ever written).
  - [ ] 5.2 Write to `ProjectSettings.GlobalizePath($"res://resources/data/abilities/{id}.json")` via `File.WriteAllText` using `ContentJson.Options` (the `MapGeneratorPanel.OnSaveAndLoadPressed` precedent, `godot/src/CreationSuite/MapGeneratorPanel.cs:246-249`; there is **no** `AbilitySerializer` — serialize inline). Write **atomically** (temp file + move) and **confirm overwrite** if `<id>.json` exists, using a Godot `AcceptDialog`/`ConfirmationDialog` (dialogs are reserved for destructive confirms per the UX spec).
  - [ ] 5.3 **Inline located-error surface** (brand-new UI — no panel does this today): on `Fail`, display `result.Error` verbatim (it already has the shape `ability '<id>'.<path>: <reason>`, naming the node/field) in a dedicated red error area near Save, and disable/block Save; on `Ok`, show a green `valid` badge (UX-DR55). Best-effort: scroll/flag the section named by the path. Do **not** modify `AbilityValidator` (Decision #6 — the located string satisfies AC3).

- [ ] **Task 6 — Wire into the Creation Suite** (AC: 1, 5)
  - [ ] 6.1 New `godot/src/Core/Bootstrap/Phases/AbilityEditorPhase.cs` cloning `TriggerEditorPhase` (but simpler — **no** `LLMService`, **no** `ScenarioDelegateBinder`): construct `new AbilityEditorPanel()`, `_ctx.Scene.AddChild(...)`, `Initialize(_ctx.Scenario, _ctx.GameState, _ctx.AbilityRegistry)`, publish `_ctx.AbilityEditorPanel`. Add a `public AbilityEditorPanel AbilityEditorPanel;` field to `SceneContext` (`godot/src/Core/Bootstrap/Phases/SceneContext.cs`, next to `TriggerPanel` ~:102).
  - [ ] 6.2 Append `new AbilityEditorPhase(_ctx)` to the phase literal (`godot/src/Core/MainScene.cs:342-366`) **AND** update `ScenePhaseOrder.Canonical` **AND** the Tier-1 `PhaseOrderTest` — the order is asserted at startup and will **throw** if these three disagree (comment at `MainScene.cs:334-341`).
  - [ ] 6.3 Add an Edit-mode-only key toggle in `MainScene._UnhandledInput` (`godot/src/Core/MainScene.cs:~483-506`) on a free key (e.g. `K`), gated by `GameMode.Edit`, calling `_ctx.AbilityEditorPanel.Toggle()` + `GetViewport().SetInputAsHandled()`. Mirror the `L`/`M`/`O` precedents. (Optional: a left-palette/MainMenu entry per UX-DR70 — not required for ACs.)
  - [ ] 6.4 The editor lists existing abilities for edit/duplicate by reading `_ctx.AbilityRegistry.All` / `.Get(i)` (`godot/src/Core/Definitions/AbilityRegistry.cs:31`) — the loaded snapshot. Use the `TriggerEditorPanel.RefreshList` / `ContentBrowserPanel.MakeCardPanel` list pattern.

- [ ] **Task 7 — "Becomes castable" verification path** (AC: 1)
  - [ ] 7.1 After a successful save, offer **"Save & Reload"** that calls `GetTree().ReloadCurrentScene()` (the `MapGeneratorPanel`/`ContentBrowserPhase.cs:81` precedent) so `_Ready` re-runs `AbilityRegistry.LoadFromDirectory` → `ResolveAbilities` and the new ability enters the registry on next load. Plain "Save" just writes the file. Be honest in tooltips: the ability is available **in the next match**, not hot-reloaded into the running one (Decision #2).
  - [ ] 7.2 AC1 end-to-end demo (for `/godot-verify`, Task 8): author the `'targeted damage'` preset → save as a new id → attach to the `alpha_faction` mage by editing its `"abilities": [...]` array (the 2.4b attach mechanism, `godot/resources/data/factions/alpha_faction.json:125`; **attach is a faction-JSON edit, not an editor feature** — Decision #3) → reload → start match → select mage → cast → effect resolves.

- [ ] **Task 8 — Determinism fence + verification** (AC: 4, 6)
  - [ ] 8.1 Confirm the fence: `AlgoVersion == 7`, `git diff` touches **no** `SimChecksum.cs`/`SystemOrderTest.cs`/golden file/version-pin; run the golden suite and assert all **9** `.golden.txt` are byte-identical (a moved golden = a leaked sim write → fix, don't re-record).
  - [ ] 8.2 `/godot-verify` the panel (build + run via Godot MCP, screenshots): Simple author+save (AC1), Advanced compose + raw-JSON round-trip (AC2), invalid → save blocked with located error (AC3), and the Task 7.2 cast demo.
  - [ ] 8.3 Full `godot.csproj` build **0 errors**; the analyzer release gate (`-p:ChimeraRelease=true --no-incremental`) clean for the new Godot-free files (`AbilityPresets.cs`, the converter `Write`); run the new + full Tier-1 suite green.

## Dev Notes

### What this story is (and is NOT)
- **IS:** a presentation-layer **authoring tool** that produces `AbilityDefinition` JSON files in `resources/data/abilities/`, validated through the existing 2.3 gate, with a simple-preset mode, an advanced multi-effect composer, and a round-tripping raw-JSON escape hatch. Active abilities only.
- **IS NOT:** a new runtime/cast path (2.4 owns that), an attach-to-unit UI (faction-JSON / the future Unit Card Editor 3.4 own that — Decision #3), a passive-ability authoring mode (Story 2.6), an AI plain-language draft generator (Story 8.6a — Decision #5), a full node-graph canvas (Trigger T3 editor / Story 7.9 — Decision #4), or any change to sim state / the checksum (AC4).

### The exact JSON contract the editor must produce (from Story 2.1/2.3 — read verbatim from source)
Top-level `AbilityDefinition` (`godot/src/Core/Definitions/AbilityDefinition.cs:20`), flat snake_case:

| JSON field | C# type | notes |
|---|---|---|
| `id` | string | non-empty; becomes the filename `<id>.json`; identity is this field, not the filename |
| `display_name` | string | |
| `targeting` | string | one of `None`/`Self`/`TargetUnit`/`GroundPoint` (resolved by `ParsedTargeting`, `:66`) |
| `cost_energy` | `Fixed` | `≥ 0` (validator checks `.Raw` sign) |
| `cost_ore` | int | `≥ 0` |
| `cost_crystal` | int | `≥ 0` |
| `cooldown` | `Fixed` (seconds) | `≥ 0` |
| `effect` | `EffectNode?` | the recursive node tree; required (an ability must declare ≥1 effect) |

**Closed effect `kind` registry (7 strings)** — `EffectNodeJsonConverter.cs:41-47`: `direct_hp_delta`, `heal`, `damage`, `apply_modifier`, `sequence`, `search_area`, `persistent`. Per-node fields:
- `direct_hp_delta`: `delta` (`Fixed`) — flat, armor-independent (Equal-Exchange self-cost shape; bypasses the damage matrix).
- `heal`: `amount` (`Fixed`).
- `damage`: `amount` (`Fixed`), `damage_type` (`DamageType` by name ∈ `{Normal,Pierce,Siege,Magic,Hero}` — **`COUNT` rejected**).
- `apply_modifier`: `modifier` (object — see below).
- `sequence`: `children` (array; **≥1**, **≤8**).
- `search_area`: `radius` (`Fixed`), `filter` (`TargetFilter` by name ∈ `{None,Self,Ally,Enemy,Neutral,Alive}` — **`Air`/`Ground`/`Structure` reserved → rejected**), `child` (single node).
- `persistent`: `initial_effect?`, `period_effect?`, `expire_effect?` (nodes), `period_ticks` (int), `period_count` (int). *(More passive-flavored — relevant but allowed at top level; the AC2 active example is `Sequence`-based.)*

**`modifier` sub-object** (`godot/src/Effects/Modifier.cs:43`, payload of `apply_modifier`): `id` (int), `duration_ticks` (int; `<0`=permanent, `0`=one-shot), `stacking` (`StackRule` ∈ `{Refresh,Stack,Ignore}` by name), `max_stacks` (int), `max_health_delta`/`attack_damage_delta`/`move_speed_delta` (`Fixed`), `status` (`StatusFlags` ∈ `{None,Stunned,Rooted,Silenced,Disarmed,Invulnerable}` by name), `period_effect?` (node), `period_ticks` (int).

**Enums serialize by exact-case name; numbers are plain JSON numbers** (`Fixed` quantizes on read). Effect objects are **closed** — any stray/duplicate property is a hard reject (`RejectUnknownProperties`, converter `:264`). `ContentJson.Options` (`godot/src/Core/Definitions/ContentJson.cs:33`): `UnmappedMemberHandling.Disallow`, converters `[JsonStringEnumConverter(null, allowIntegerValues:false), FixedJsonConverter, EffectNodeJsonConverter]`, no `WriteIndented`, no naming policy (explicit `[JsonPropertyName]`).

**Shipped samples to round-trip-test (Task 1.3)** — `godot/resources/data/abilities/`: `fireball.json` (`sequence`→`damage`+`search_area`→`damage`), `minor_heal.json` (single `heal`, omits optional costs → defaults), `battle_fury.json` (`apply_modifier` with stat deltas).

### The validator + the AC3 error surface
`new AbilityValidator().Validate(AbilityDefinition? def)` (`godot/src/Core/Definitions/AbilityValidator.cs:35`) → `AbilityValidationResult { bool Ok; string? Error; Validated<AbilityDefinition> Value }` (`AbilityValidationResult.cs:12`). **Pure — never throws, never logs; stops at the FIRST failure.** On `Ok` it mints `Validated<AbilityDefinition>` via `ScenarioValidator.Proof` (the editor **cannot** mint one itself — `ValidatedSoleMinterTest` allow-lists only `ScenarioValidator.cs` + `AbilityValidator.cs`; route through `Validate`). `Error` is a **single located string** `ability '<id>'.<path>: <reason>` where `<path>` is dotted/indexed (`effect.children[2].child`, `effect.modifier.period_effect`). **There is no structured field locator** — AC3 is met by displaying this string (Decision #6). Checks order: id/targeting → cost/cooldown ≥0 → `effect` not null → `EffectBounds.Validate` (depth≤8, seq children≤8) → `WalkGraph` (`MaxTotalEffectNodes=64`, `MaxSearchAreaDepth=2`, Persistent re-entrancy, 0-child Sequence). Fail-closed loader for the raw-JSON pane: `AbilityLoader.Load(json, sourceLabel)` (`AbilityLoader.cs:22`) — never throws/null; folds parse errors into the same located shape.

`EffectCaps` values to enforce in-UI (`godot/src/Effects/EffectCaps.cs`): `MaxEffectDepth=8`, `MaxSequenceChildren=8`, `MaxSearchTargets=64`, `MaxSearchAreaDepth=2`, `MaxTotalEffectNodes=64`, `MaxModifiersPerEntity=8`.

### 🚨 The load-bearing gap — `EffectNodeJsonConverter.Write` throws today (this story owns it)
`EffectNodeJsonConverter.Write` (`godot/src/Core/Definitions/EffectNodeJsonConverter.cs:63-65`) currently `throw new NotSupportedException("Serializing an EffectNode is not supported in Story 2.3 (Read-only). Authoring round-trip lands with the Ability Editor (Story 2.5).")`. There is **no reflection-based Write fallback** (custom converter), so `JsonSerializer.Serialize<AbilityDefinition>(def, ContentJson.Options)` throws the instant it reaches `effect`. **Completing this Write is Task 1.1** and is the keystone enabling AC2 (raw-JSON view) and the Tier-1 round-trip test. It is **determinism-safe**: serialization is authoring-only, never called inside a tick or `SimChecksum.Compute` (AC4 holds). Keep `Read` byte-for-byte unchanged.

**`Fixed` round-trip precision note:** `FixedJsonConverter.Write` emits `value.ToFloat()`; canonical identity is `Fixed.Raw` (16.16). For **authored magnitudes** (small ints/decimals like `80`, `6`, `4`, `1`) the value→float→value path round-trips exactly. Judge round-trip identity on the **reparsed graph's `Fixed.Raw`**, not on text equality (the Tier-1 assertion). This is acceptable for the authoring value ranges and matches how the samples are written.

### Template to clone + the UI reality
- **Shell:** `godot/src/CreationSuite/TriggerEditorPanel.cs` — `partial class : Node` that builds its **own** `CanvasLayer { Layer = 12 }` in `BuildUi()`; right-anchored `PanelContainer`; lifecycle `Initialize`/`Toggle`/`Update`/`_Ready`→`BuildUi`/`OnModeChanged`. **It does NOT save to disk** (it mutates in-memory `ScenarioData.Triggers[]`) — do not copy its persistence.
- **Disk-save:** `godot/src/CreationSuite/MapGeneratorPanel.cs:246-249` (`ProjectSettings.GlobalizePath` + serialize + `File.WriteAllText`).
- **Reusable patterns (ad-hoc, copy-pasted — there is NO Theme `.tres` / design kit yet; Epic 3 builds it):** `SettingsPanel.AddSliderRow`/`AddToggleRow`/`AddSectionHeader` (`godot/src/UI/SettingsPanel.cs:230/272/221`); `ContentBrowserPanel.MakeCardPanel`/`MakeTabButton`/`SwitchTab` (`godot/src/UI/ContentBrowserPanel.cs:837/214/197`); `MainMenuOverlay.AddMenuButton` (`godot/src/UI/MainMenuOverlay.cs:184`). **No `OptionButton`/`SpinBox` wrapper exists** → the targeting/kind dropdowns and numeric fields are net-new raw Godot controls, styled inline (dark navy bg, blue-grey border, accent-blue titles). **Inline located-error UI is brand-new** — no panel surfaces a field-located error today; the backend (`AbilityValidationResult.Error`) is ready.
- **House style intent (UX-DR70/DR54):** right-dock active-editor panel with a Simple/Advanced **segment pill** at the top; Advanced reveals extra fields + slider min/max + the raw-JSON pane; inline error badges **block Save**; tooltip on every control (NFR-2). The formal component kit is Epic-3 (UX-DR33 says "compose from the kit" — but the kit isn't code yet, so compose from the current ad-hoc conventions and **log** that the kit is pending; this is a known sequencing reality, not a violation).

### Wiring (exact, order-asserted)
- Phase literal: `godot/src/Core/MainScene.cs:342-366` (append `AbilityEditorPhase`). **Must** also edit `ScenePhaseOrder.Canonical` **and** Tier-1 `PhaseOrderTest`, or startup throws (comment `MainScene.cs:334-341`).
- Phase template: `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` (clone, drop the LLM + `ScenarioDelegateBinder`). Publish on `SceneContext` (`SceneContext.cs:~102`, beside `TriggerPanel`).
- Key toggle: `MainScene._UnhandledInput` (~`:483-506`), Edit-mode-gated, free key (e.g. `K`).
- Registry to list existing abilities: `SceneContext.AbilityRegistry` (`SceneContext.cs:58`, `= AbilityRegistry.Empty`; set `MainScene.cs:322`) → `.All`/`.Get(i)`/`.Count`.

### "Becomes castable" — the honest mechanism (Decision #2)
`AbilityRegistry.LoadFromDirectory(ProjectSettings.GlobalizePath("res://resources/data/abilities/"))` runs **once at `_Ready`** (`MainScene.cs:263` client / `:1200` server), indexes by **ascending ordinal `Id`** (`AbilityRegistry.cs:45` — the MP-determinism guarantee), is injected into `SimulationHost.Create` (`MainScene.cs:294`) and held privately in `AbilityCastSystem` (`SimulationHost.cs:99`). **No hot-reload / FileSystemWatcher exists, and Edit→Play is only a `GameState` flag flip** (`GameState.cs:44`) that reuses the built registry. The **only** way a newly-saved file enters the registry is a full scene reload (`GetTree().ReloadCurrentScene()` — the MapGenerator/ContentBrowser precedent, `MainScene.cs:1081`/`ContentBrowserPhase.cs:81`). So AC1's "castable in a match" = **castable in the next match after save + reload**. Building a live in-match rebuild hook is **out of scope** (it would re-resolve all faction defs + patch the host's `AbilityCastSystem` — disproportionate for an authoring story and determinism-risky).

### Determinism & regression posture (AC4)
- **NO fold.** `AlgoVersion` stays **7**. `SimChecksum.cs` and `SystemOrderTest.cs` untouched. No new golden. All **9** goldens byte-identical. Precedent: Story 2.3 (load-time-only, AlgoVersion stayed at its then-value, goldens byte-identical) and Story 2.4b (presentation/UI, explicit "no fold" fence). Rule: [[chimera-checksum-fold-timing-rule]] — fold only when an array first goes mutable mid-match; this story adds no array.
- A moved golden ⇒ a leaked sim write (the converter `Write` was wrongly invoked in a sim path, or a `float` crept into sim code) ⇒ **fix the leak, do not re-baseline.**
- No `Fixed.FromFloat` outside the converter; no `float` keyword in any new Godot-free file (analyzer CHM0001/CHM0005); no `System.Random`/`DateTime`/`Dictionary`-enumeration in sim code. The new Godot-free files (`AbilityPresets.cs`, converter `Write`) ride the analyzer release gate. **Write `Fixed` fields by delegating to the registered `FixedJsonConverter` (e.g. `JsonSerializer.Serialize(writer, node.Delta, options)`), not a hand-rolled `node.Delta.ToFloat()` in `EffectNodeJsonConverter.Write` — the latter trips CHM0005 (ToFloat outside the converter).**
- **Known MP gap (NOT this story — Epic 9):** authored ability/faction JSON is determinism-relevant but lives *outside* the pre-match content hash (2.4b-review deferral). A divergent/missing abilities dir across peers surfaces as an opaque `HALT(NoMajority)`, not a clear content-mismatch error. 2.5 authoring is local/single-player, so this is not a blocker — but do **not** assume editor output is MP-integrity-safe; content-hash coverage is Story 9.9's job.

### Testing standards
- **Tier-1 (Godot-free xUnit, `godot/ProjectChimera.Sim.Tests/Definitions/`)** — the 2.3 precedent (6 files already there). New: `AbilityRoundTripTests` (serialize↔`Load` `Fixed.Raw`-equality incl. the 3 samples), `AbilityPresetTests` (each preset → `Validate` `Ok`; preset→serialize→reparse identical). `SimSources.props` already globs `src/Core/Definitions/**` — verify, no edit. Every gate ships with teeth (inject a violation → RED → revert), per the retro action item.
- **Tier-2 / GdUnit4 does NOT exist** (`godot/tests/` empty, zero GdUnit4 refs). The Godot `AbilityEditorPanel` Control is verified via **`/godot-verify`** (Godot MCP build/run/screenshot) — the 2.4b template (its testable C# went to a Tier-1 `AbilityWiringTeethTest`; the panel itself was screenshot-verified).

### Project Structure Notes
- New files: `godot/src/CreationSuite/AbilityEditorPanel.cs` (Godot Control — presentation), `godot/src/Core/Definitions/AbilityPresets.cs` (Godot-free — sim/content), `godot/src/Core/Bootstrap/Phases/AbilityEditorPhase.cs` (Godot — wiring), Tier-1 tests under `godot/ProjectChimera.Sim.Tests/Definitions/`.
- Edited files: `godot/src/Core/Definitions/EffectNodeJsonConverter.cs` (complete `Write`), `godot/src/Core/MainScene.cs` (phase literal + key toggle), `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` (Canonical), `godot/src/Core/Bootstrap/Phases/SceneContext.cs` (handle field), the Tier-1 `PhaseOrderTest`. For the AC1 demo only: `godot/resources/data/factions/alpha_faction.json` (mage `abilities`) — revert or keep per the verify run.
- Naming: `PascalCase.cs` matching class; namespaces `ProjectChimera.CreationSuite` (panel), `ProjectChimera.Core.Definitions` (presets), `ProjectChimera.Core.Bootstrap` (phase). `#nullable enable` per file. The Godot panel is `partial`.
- Layer discipline: the panel reads sim/content data (`AbilityRegistry`) and **writes JSON files only** — it never mutates sim arrays (sacred boundary). `AbilityPresets`/the converter `Write` are pure C# (no `using Godot;`).

### Project Context Rules
- **Layered complexity / progressive disclosure** (the platform pillar, GDD §"Layered complexity"): every creator-facing system ships a simple mode (presets/dropdowns) AND an advanced mode (compose/raw-JSON). This story is the canonical instance.
- **No gameplay logic, balance number, or rule hardcoded where a creator can't reach it** — the editor authors *data*; the closed effect vocabulary is the only surface (no scripting escape hatch — *ever*; GDD/architecture "no JASS/Lua/RunScript/customParams").
- **`Fixed` end-to-end, convert at parse only** — never `Fixed.FromFloat` outside the converter; content numerics are `Fixed` via `FixedJsonConverter`.
- **Server-validatable content (NFR-6)** — every shareable construct must pass the static `Validated<T>` gate before it can run; the editor enforces this on save (fail-closed, no invalid file written).
- **Composition over inheritance** — abilities compose from orthogonal effect primitives, not subclasses.
- **Reuse existing systems** — `AbilityDefinition`/`AbilityValidator`/`AbilityLoader`/`ContentJson.Options`/`AbilityRegistry`/`EffectCaps` (2.1/2.3/2.4), and the `TriggerEditorPanel`/`MapGeneratorPanel`/`SettingsPanel` UI patterns; do not build parallel ones.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story-2.5 (lines 930–946)] — story, ACs, implementation note ("follows TriggerEditorPanel pattern… outputs the same AbilityDefinition JSON… no new runtime path… active abilities only; passive is 2.6").
- [Source: _bmad-output/planning-artifacts/epics.md#Epic-2 (line 836–840)] — epic objective + brownfield sequencing note.
- [Source: _bmad-output/planning-artifacts/epics.md#UX-DR54/DR70/DR55/DR33/DR24/DR77] — simple/advanced disclosure + raw-JSON; Creation Suite shell right-dock; inline errors block save; compose-from-kit; segment pill; sibling Unit Card Editor.
- [Source: _bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md#FR-8/FR-10/NFR-6] — active ability authoring without code; presets + multi-primitive compose; statically validatable content.
- [Source: _bmad-output/game-architecture.md#N4 (line 2042)] — closed-`kind` converter, fail-closed deserialize, `[JsonPolymorphic]` forbidden, no escape hatch.
- [Source: godot/src/Core/Definitions/AbilityDefinition.cs, AbilityTargeting.cs, EffectNodeJsonConverter.cs, ContentJson.cs, AbilityValidator.cs, AbilityValidationResult.cs, AbilityLoader.cs, AbilityRegistry.cs] — the contract, converter (incl. the `Write` `NotSupportedException` at `:63`), validator + error shape.
- [Source: godot/src/Effects/EffectNode.cs, DirectHpDeltaEffect.cs, HealEffect.cs, DamageEffect.cs, ApplyModifierEffect.cs, SequenceEffect.cs, SearchAreaEffect.cs, PersistentEffect.cs, Modifier.cs, TargetFilter.cs, EffectCaps.cs] — node fields, enums, caps.
- [Source: godot/src/CreationSuite/TriggerEditorPanel.cs (shell, `:314-317` JSON-print), MapGeneratorPanel.cs (`:246-249` disk-save); godot/src/UI/SettingsPanel.cs, ContentBrowserPanel.cs, MainMenuOverlay.cs] — UI patterns to clone.
- [Source: godot/src/Core/MainScene.cs (`:342-366` phase literal, `:334-341` order-assert comment, `:263/:294/:322` registry build/inject/publish, `:1081` reload, `:483-506` key toggles); src/Core/Bootstrap/Phases/{TriggerEditorPhase.cs, SceneContext.cs}; GameState.cs (`:44`); SimulationHost.cs (`:99`)] — wiring + the no-hot-reload reality.
- [Source: godot/src/Core/SimChecksum.cs (`:68` AlgoVersion=7); godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt (9 files); SimSources.props; ProjectChimera.Sim.Tests/Definitions/*] — determinism fence + Tier-1 test home.
- [Source: _bmad-output/implementation-artifacts/2-3-…md, 2-4a-…md, 2-4b-…md, deferred-work.md] — no-fold precedents, attach mechanism, `Write`-deferred-to-2.5 hand-off, `/godot-verify` UI-test template.

## Dev Agent Record

### Agent Model Used

_(to be filled by dev-story)_

### Debug Log References

### Completion Notes List

### File List

---

## Open Decisions / Questions for Alec (defaults baked in — confirm or redirect at review)

> All seven are **resolved-by-default** in the story above; flagging the consequential ones so you can redirect before dev. None is hard to reverse at this stage.

1. **Round-trip strategy / the `Write` path (architecture-defining).** Default = **complete `EffectNodeJsonConverter.Write`** (Godot-free, authoring-only) so the canonical `ContentJson.Options` round-trips both ways; the structured editor builds an in-memory `AbilityDefinition` and the raw-JSON pane serializes/parses it. 2.3 explicitly deferred this Write *to 2.5 by name*. Alternative was raw-JSON-as-source-of-truth (no Write), but that weakens the "simple preset, never touch JSON" experience. **Determinism-safe** (never in a tick).
2. **"Immediately castable" = save + scene-reload, NOT hot-reload (AC1 interpretation).** Default = the editor offers "Save & Reload" (`ReloadCurrentScene()`, the MapGenerator precedent); the ability is castable in the **next** match. Building an in-match hot-reload hook is out of scope. Confirm this reading of AC1's "immediately."
3. **Create-only (no attach UI).** Default = the editor creates the ability file; **attaching to a unit is a faction-JSON edit** (2.4's domain; the future Unit Card Editor 3.4 will do it via UI). AC1's "attachable to a unit (2.4)" is verified by attaching the output to the existing mage. Confirm we don't add an attach UI here.
4. **Advanced mode = structured effect-tree builder, not a node-graph canvas.** Default = a tree/list composer (the effect graph is a strict tree) + the raw-JSON pane for depth. The GraphEdit node canvas is the Trigger T3 editor's domain (Story 7.9). Confirm.
5. **⚠ AI plain-language authoring is DEFERRED to Story 8.6a (contradicts the [[chimera-ability-authoring-ai-transparency-vision]] note).** Your captured vision is creators typing abilities in plain language and the AI rephrasing into our real fields *in front of them*. The epic scopes 2.5 as **manual** preset/compose/raw-JSON, and AI draft-gen depends on the Epic-8 `ILLMProvider` stack (not built). Default = **build the manual editor now (2.5), AI-ready**; the AI draft layer rides on top in 8.6a (it generates a draft `AbilityDefinition` this editor then loads/edits/confirms). **If you want the AI layer in 2.5, say so** — it would add a hard dependency on Epic 8 and likely force a split.
6. **Inline error = surface the validator's located string (no validator change).** Default = display `AbilityValidationResult.Error` (already names node+field) and block Save; no structured locator added. Confirm AC3 is met by the string (vs per-widget highlighting, which would mean extending the validator).
7. **Split recommendation (size).** This is a large story (new panel + advanced composer + raw-JSON + Write path + wiring). Clean split: **2.5a** = core (`Write` + presets + Tier-1) + Simple mode + validate-gated save + raw-JSON escape hatch + wiring (shippable: author via preset or raw-JSON, validated & castable); **2.5b** = the advanced *structured* multi-effect tree builder (Task 3). 2.4 was split at similar size. Default = **author as one story with Task 3 marked as the split line**; say the word to split now via `gds-correct-course`.
