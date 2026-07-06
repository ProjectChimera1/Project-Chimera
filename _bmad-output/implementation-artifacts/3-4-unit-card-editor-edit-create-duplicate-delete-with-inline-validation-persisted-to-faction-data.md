---
baseline_commit: f7a54ef30bf137e74a4425d97619bc15214f3a24
---

# Story 3.4: Unit Card Editor — edit/create/duplicate/delete with inline validation, persisted to faction data

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a creator,
I want to create, edit, duplicate, and delete unit definitions in the Unit Card Editor with no JSON, validated inline before save,
so that I can author units safely and have them persist into my scenario's faction data.

## Context & Scope — read first

This is the **editing half** of the Unit Card Editor (UX-DR77). Story 3.3 shipped the **read-only** card (`UnitCardPanel.cs`) that browses a faction's `Units` and renders each one from the Story-3.1 component kit. **3.4 makes that same panel editable** — the read-only `Readout`s become editable inputs, and the panel gains Save / Create / Duplicate / Delete, inline fail-closed validation with per-field badges, a simple/advanced disclosure with a raw-JSON escape hatch, undo/redo, and **write-back to the faction JSON on disk**.

**Extend the 3.3 panel in place — do not build a second panel.** `UnitCardPanel` already holds the live `FactionDefinition`, browses `_faction.Units` with a `_index` cursor, self-initializes the kit, owns the 3D preview, and is wired into `MainScene` on the `J` toggle. 3.4 adds an edit surface to it; it does **not** clone the browse/preview/kit-bootstrap. [Source: `UnitCardPanel.cs:46-49,81-90,300-331`; 3-3 story File List]

Four hard truths that shape this story (each verified against live source — see Dev Notes):

1. **Persistence is a targeted JSON-DOM patch, NOT a whole-object re-serialize.** A unit is not its own file — it lives inside a faction file's `units[]` array alongside other units, buildings, and faction metadata. Re-serializing the whole `FactionDefinition` back through reflection **corrupts the file in eight ways** (dumps six computed `Parsed*` getters as integer fields, drops the `signature_mechanic`/`deferred_mechanics` keys, balloons every unit with defaults, reorders fields, collapses formatting…). The safe path is to parse the raw file with `System.Text.Json.Nodes.JsonNode`, patch only the one unit object, and atomic-write. See Decision **D-1** and the Dev Notes "Persistence recipe." **This is the single highest-risk part of the story.**
2. **No unit validator exists — 3.4 builds the first one.** `UnitTagValidator.cs:8-9` states it verbatim: *"Units are NEVER content-validated today (no FactionValidator/UnitValidator exists)."* The lenient faction loader silently **fail-opens** bad enums (`ParsedCategory` unknown → `Melee`, etc.). 3.4 adds a Godot-free `UnitDefinitionValidator` that rejects out-of-range/negative/invalid values with a **located** message. This closes a real defect class parked since 1.3b/2.9b (negative `cost_crystal` *adds* crystal each train). See Decision **D-9**.
3. **The UX-DR55 per-field located badge is a net-new component** — nothing in the kit is a validation badge (`ChimeraMark` is the decorative Seal; the AbilityEditor uses a single panel-level status line). 3.4 introduces it, and because stories 3.5/3.6/3.7 all need the same "located badge on the offending field," it should be a small reusable kit helper (UX-DR33 "log the missing primitive"). See Decision **D-4**.
4. **Undo/redo and Save do not auto-collide, but the wiring must be careful.** `EditorHistory` is command-pattern and 3.4 gets its own instance; but unlike `EntityPlacer` (whose undo is auto-reflected because presentation polls sim arrays each frame), 3.4's form is built-once widgets — **every undo/redo delegate must also call the form `Refresh()`**, and Ctrl+Z must be gated on panel visibility so it doesn't fire `EntityPlacer`'s history too. See Decision **D-6**.

**Determinism posture — PURE AUTHORING-TIME, zero fold.** Editing a content POCO and rewriting a JSON file touches **no** `EntityWorld`/store/`HeroStore`/sim array and moves **no** checksum. `CanonicalModelHash` folds `ScenarioData` and references factions by **path-string + unit-id string, never by unit stats** (`CanonicalModelHash.cs:60,95`), so editing hp/damage/cost moves no hash. **No committed golden loads the real faction files** (all build their factions in-code; the sole real-file loader `CanonicalScenarioTests.cs:59` pins nothing — it is a run-twice-equal self-consistency check). Stamps stay **9 / 3 / 1 / 2 + StartStateHash 1**; all **18 goldens byte-identical**. The only `src/Core` touches are new **Godot-free, additive** files (`UnitDefinitionValidator.cs` + faction-save helper), analyzer-clean like the existing definition POCOs. This is the 3.3 posture, not a sim story. [Source: `CanonicalModelHash.cs:40-102`; `GoldenApplierScenario.cs:68-86`; [[chimera-checksum-fold-timing-rule]]]

### Scope fence (explicitly OUT of 3.4 — do not build)

| Deferred capability | Owner story | Source |
|---|---|---|
| Model **browse** dialog / **live-preview re-render on assign** / explicit "box placeholder" *button* | 3.5 | epics.md:1273-1287 |
| Archetype + ability/behavior **composition authoring** (structured ability-picker, add/remove/choose abilities); new definition fields | 3.6 | epics.md:1289-1305 |
| **Promote-to-Hero** switch (setting `is_hero`), leveling curve, XP, signature/ultimate slots | 3.7 | epics.md:1307-1323 |
| **No-restart edit→play round-trip** (F5 loop, match-state reset scope, ≤2s) | 3.10 | epics.md:1359-1371 |
| Editing **Buildings**, the **beta/other-slot** faction, or a scenario-slot faction (multi-faction select flow / Faction Definer) | 3.6 / Epic 5 | epics.md:499-501; 3-3 D-10 |
| Structured **combat_feedback** sub-form (authored via the raw-JSON hatch in 3.4; presentation-juice, not core) | (raw-hatch only) | this story D-3/D-5 |
| A `user://` writable authoring home (dev-time `res://` write only, same limit the AbilityEditor ships with) | later | Agent-6 recon |

> 3.4 makes `mesh_path` an **editable text field + validation**; 3.5 adds the browse UI + live re-render. 3.4 exposes `category`/`damage_type`/`armor_type` as **dropdowns + validation** and validates ability refs; the deeper **ability composition** and **hero promotion** authoring are 3.6/3.7. `is_hero` stays **read-only** in 3.4 (the HERO tag from 3.3) — the Promote-to-Hero switch that sets it is 3.7.

## Acceptance Criteria

**AC1 — edit + create/duplicate/delete, persisted, with undo** *(epics.md:1263)*
**Given** the read-only Unit Card panel from 3.3 **When** I edit stat/combat/cost/model fields and click **Save** **Then** the `UnitDefinition` is updated and **written to the faction JSON referenced by the scenario** (no manual JSON editing), **And** **Create / Duplicate / Delete** operations add, clone, and remove unit definitions in that faction, **And** edits route through the existing **`EditorHistory`** undo/redo stack so **Ctrl+Z reverts a unit edit** (and Ctrl+Y re-applies it), with the form re-rendering to match.

**AC2 — inline fail-closed validation with located badges** *(epics.md:1265)*
**Given** an invalid edit (out-of-range or missing stat, missing/invalid model path, undefined ability reference, invalid archetype/category) **When** I attempt to **Save or Playtest** **Then** inline validation (**AR-39, fail-closed**) **blocks the action** and shows a **located error badge (UX-DR55) on the offending field** describing the problem, **And** a unit with all-valid fields **saves with no badges** and is **immediately usable in playtest**.

**AC3 — simple/advanced disclosure + raw-JSON hatch + tooltips** *(epics.md:1267)*
**Given** the simple/advanced disclosure (**UX-DR54**, the `Segment` control) **When** I toggle to **advanced** mode **Then** every authorable field is exposed **including a raw-JSON escape hatch for the unit definition**, and simple mode hides advanced fields behind the disclosure, **And** every control carries a hover-**and**-keyboard-focus tooltip (**UX-DR53 / NFR-2**).

**Covers:** FR-1, FR-2, FR-6, FR-7, AR-39, AR-3, UX-DR77, UX-DR54, UX-DR55, UX-DR53. **Depends on:** 3.3. [Source: epics.md:1269]

### Additional acceptance (derived from the "Covers" requirements + baked decisions)

- **AC4 (round-trip fidelity — the persistence teeth):** after Save, the faction file **still parses** on the lenient loader; **every other unit, every building, and the faction-level keys** (`signature_mechanic`, `deferred_mechanics`, `color`, `id`, `display_name`) are **preserved byte-for-byte** (only the edited unit's changed properties differ); no `Parsed*` computed getter and no default-ballooned field is emitted. Proven by a Tier-1 DOM-patch round-trip test on a fixture faction. [D-1]
- **AC5 (determinism/regression):** Tier-1 suite green (incl. `PhaseOrderTest` — unchanged; no new phase), full `godot.csproj` build 0-err, all **18 goldens byte-identical**, stamps **9/3/1/2 + StartStateHash 1** untouched, release analyzer gate 0-err / RS0030 zero-baseline held (the new `UnitDefinitionValidator` + save helper are Godot-free `src/Core` and analyzer-clean).
- **AC6 (in-engine, `/godot-verify`):** in-engine, with the card open in Edit mode: edit a stat and Save → the file on disk reflects the change; Create adds a unit, Duplicate clones it with a new id, Delete (after confirm) removes it; an out-of-range/negative-cost/invalid-enum/undefined-ability/unresolvable-mesh edit shows a **located badge** and **blocks Save**; toggling Advanced reveals the extra fields + raw-JSON pane; every control shows a tooltip on hover and on keyboard focus.

## Decisions (recommended defaults — confirm with Alec)

All baked into the Tasks/ACs below as the **recommended default**; flip any before or during dev.

- **D-1 — Persistence = targeted JSON-DOM patch (STRONGLY recommended; the reflection alternative is unsafe).** Rewrite-back the edited unit by parsing the **raw faction file** with `System.Text.Json.Nodes.JsonNode`, locating the `units[]` element whose `id` matches, and setting **only the changed properties** on that `JsonObject` (append a new object for Create, clone one for Duplicate, remove one for Delete). Serialize with `new JsonSerializerOptions { WriteIndented = true }`, then write atomically (`.tmp` → `File.Move(overwrite:true)`) with a re-parse self-check — reusing the AbilityEditor's `WriteFile` skeleton (`AbilityEditorPanel.cs:553-580`). *Alt (rejected): deserialize `FactionDefinition` → mutate → `JsonSerializer.Serialize` the whole object — corrupts the file 8 ways (see Dev Notes "Persistence recipe"). If ever chosen it first requires adding `[JsonIgnore]` to all six `Parsed*` getters + a `[JsonExtensionData]` bag + `WriteIndented` + `DefaultIgnoreCondition.WhenWritingDefault` — strictly more work and more fragile.* **In plain terms:** we surgically edit the one unit's lines in the file and leave everything else exactly as the creator wrote it, instead of re-printing the whole file from our in-memory objects (which would silently drop hand-written bits and add junk).
- **D-2 — Entry point = extend the 3.3 `UnitCardPanel`, in Edit mode, always-editable.** The card's fields ARE the editable inputs when the panel is open in Edit mode — no separate "view vs edit" sub-toggle beyond the simple/advanced disclosure. Reuse the existing `_faction`/`_index` browse, the preview, and the kit bootstrap; add a toolbar + editable regions. *Alt: a separate `UnitEditorPanel` (duplicates browse/preview/kit-init — rejected).*
- **D-3 — Simple/Advanced disclosure = `ChimeraTabs.Create(TabsVariant.Segment, "Simple", "Advanced")`, NOT `ChimeraSwitch`.** Explicit 3.1c design ruling: the **segment** pill-group is the disclosure UI; `ChimeraSwitch` is the boolean-field-reveal primitive (reserved for 3.7 Promote-to-Hero). **Simple** = the AC-named core fields (the 3.3 display set, now editable). **Advanced** = simple + the deferred flat fields (`armor`, `train_time`, `splash_radius`, `collision_radius`, `separation_priority`, `mesh_scale`, `max_energy`) + comma-list fields (`prerequisites`, `tags`, `attack_domains`) + the raw-JSON escape hatch. *Alt: a `switch` (violates the 3.1c ruling).*
- **D-4 — UX-DR55 badge = a small reusable kit helper (recommended), composed from `Tag(Danger)` + `ChimeraTooltip`.** Add a `ChimeraValidationBadge` (or a `ChimeraComponents.ValidationBadge(field, message)` helper) to `src/UI/Components/` that anchors a `TagVariant.Danger` pill to a field control and carries the full sentence in a `ChimeraTooltip(Field)`; a matching `TagVariant.Ok` "valid" state. **Log this per UX-DR33** (a missing primitive proven by 4-story reuse: 3.4/3.5/3.6/3.7). *Alt: compose ad-hoc per field with no shared component (more duplication across 3.5-3.7 — rejected).*
- **D-5 — Raw-JSON hatch = a multiline `TextEdit` over the SINGLE unit's JSON (advanced only).** Kit `Input` is single-line; mirror the AbilityEditor's rolled-own `_jsonPane` `TextEdit` (`AbilityEditorPanel.Advanced.cs:91`), styled from theme tokens. It shows/edits the current unit's JSON object only (not the whole faction). On Save, if the pane is dirty the pane wins (validated fail-closed, then folded back into the form); else serialize the form model. Carry the `_paneDirty`/`_suppressPaneDirty`/"don't clobber on reveal" guards (`AbilityEditorPanel.Advanced.cs:92,105-110,189-195`). *Alt: no raw hatch (violates AC3 "including a raw-JSON escape hatch").*
- **D-6 — Undo/redo = a private `EditorHistory` instance, every entry re-renders, Ctrl+Z gated on visibility.** Reuse `EditorHistory` as-is (`Push(redo, undo)`; `Undo`/`Redo`). Wrap each entry so **both** delegates also call `Refresh()` (the form is built-once widgets — it won't update itself, unlike `EntityPlacer`). Route Ctrl+Z/Ctrl+Y through **this** history only when the panel is visible, and `SetInputAsHandled()` so `EntityPlacer`'s history (also on Ctrl+Z, `EntityPlacer.cs:202-216`) doesn't double-fire. Undo/redo operate on the in-memory model + form; **Save** is the separate persistence action (undo does not rewrite the file per keystroke). *Alt: snapshot-based history (heavier; rejected — command-pattern matches the existing class).*
- **D-7 — Create/Duplicate/Delete semantics + hard duplicate-`Id` reject.** Create = a new `UnitDefinition` with type defaults + a unique auto-id (`new_unit`, `new_unit_2`, …); Duplicate = clone the current unit with a unique id (`<id>_copy`); Delete = a `ChimeraDialog` danger-confirm then remove. **A duplicate `Id` is hard-rejected** at create/duplicate/save (the list index is load-bearing: `IndexOfUnit == EntityWorld.MeshType` render slot, `FactionDefinition.cs:49-54`), plus `SanitizeId(id) == id` and non-empty id are enforced (mirrors the 3.2 HeroStore `Mint` dup-reject lesson and the AbilityEditor id-guard). Uniqueness scope = the faction's `Units` (Buildings out, D-10 from 3.3). *Alt: allow dup ids / silent rename (rejected — breaks the render-slot mapping).*
- **D-8 — Target faction/slot = default P1 alpha, Units only, path threaded from the phase.** Edit `_ctx.FactionDef` (alpha) and write `MainScene.P1_FACTION_JSON` (`res://resources/data/factions/alpha_faction.json`), or `_ctx.Scenario.PlayerSlots[0].FactionJson` when a scenario is loaded. Thread that **res:// path** into the panel via `UnitCardPhase` (the panel today holds only the parsed def, not its path). *Alt: a multi-faction select flow (Epic 5 Faction Definer — out of scope).*
- **D-9 — Validation = a new Godot-free `UnitDefinitionValidator` returning ALL located field errors (not first-fail).** Home it in `src/Core/Definitions` (Tier-1-testable), modeled on `AbilityValidationResult`'s shape but returning a **list** of `(fieldPath, message)` so every offending field badges at once (a deliberate divergence from `ScenarioValidator`/`AbilityValidator`'s first-fail, for the per-field-badge UX). It does **NOT** mint `Validated<UnitDefinition>` (no applier consumes such a token; authoring-time gate only — like `UnitTagValidator`, which is a lightweight fail-closed DROP), so the `ValidatedSoleMinterTest` allow-list is untouched. Rules in Dev Notes. The one rule needing Godot (`mesh_path` resolvability via `ResourceLoader.Exists`) is a thin presentation-side check layered on top. *Alt: reuse the first-fail single-`Error` shape (one badge at a time — worse UX; rejected).*
- **D-10 — Save UX = persist to file (authoritative) + mutate the in-memory bound def; "usable in playtest" on next Play/match.** Because content only enters the registry on scene load and a scenario's live match may use a *different* `FactionDefinition` instance (`_ctx.SlotFactionDefs`, `ScenarioLoadPhase.cs:101`) than the card's `_ctx.FactionDef`, the file is the source of truth. 3.4 also mutates the in-memory bound `FactionDefinition.Units` so the default (non-scenario) path's next Play reflects edits without an app restart; the fully-seamless no-restart round-trip is **3.10**. Status line states "saved — applies on next playtest/match." An optional **Save & Reload** button (like the AbilityEditor's) forces `ReloadCurrentScene()`. *Alt: claim live hot-patch (dishonest about the instance-aliasing — rejected).*
- **D-11 — Playtest gate scope = block Save always; block Edit→Play while the card is open with invalid fields.** When the card holds invalid field values, disable/deny Save and intercept the Edit→Play transition (F5 / `SetMode(Play)`) with badges shown; when all-valid, both proceed ("immediately usable in playtest"). Don't hijack the global F5 when the card is closed or clean. Validate **on the Save/Playtest action** (the AC requirement); optionally also live-validate on each field commit to badge proactively (lightweight enhancement). *Alt: Save-only gate (ignores AC2's literal "or Playtest").*

## Tasks / Subtasks

- [x] **Task 1 — Thread the faction file path + make the panel edit-aware (AC1; D-2/D-8)**
  - [x] In `UnitCardPhase.Run()` pass the source `res://` path into the panel: extend `UnitCardPanel.Initialize(...)` (or add a setter) with `string factionJsonPath` sourced from `_ctx.Scenario?.PlayerSlots[0].FactionJson ?? MainScene.P1_FACTION_JSON` (`UnitCardPhase.cs:26`; `ScenarioData.cs:28-30`; `MainScene.cs:182-183`). Store it on the panel for write-back. (`LoadFactionFromPath` already resolves a res:// path for the standalone harness — retain it there too.)
  - [x] Add panel edit state: the current `UnitDefinition` being edited is `_faction.Units[_index]` (the live reference — edits mutate it in place, D-10). Keep `Buildings` out (D-10).
  - [x] No new phase, no `SceneContext` field, no `ScenePhaseOrder`/`PhaseOrderTest` change (3.4 extends the existing panel/phase — `PhaseOrderTest` stays green unchanged).
- [x] **Task 2 — Editable regions: readouts → inputs (AC1, AC3; D-2/D-3)**
  - [x] Convert the 3.3 body regions to editable controls bound to the current def, **simple mode**: `Input` for `id`/`display_name`/`mesh_path`; `Select` for `category`/`damage_type`/`armor_type` (closed enum item lists); `NumInput` for `hp`/`speed`/`attack_damage`/`attack_range`/`attack_speed`/`vision_range` (float) and `cost_ore`/`cost_crystal`/`supply` (int). Bind `NumInput` directly to the `float`/`int` props (skip Fixed converters — the sim quantizes once at spawn). Each control writes back to the def on change and pushes an `EditorHistory` entry (Task 7). [`ChimeraComponents` Input/Select/NumInput; field map in Dev Notes]
  - [x] Keep the read-only header (name/id/archetype tag + **HERO tag stays read-only** — Promote-to-Hero is 3.7) and the 3D preview intact. `is_hero`, structured ability authoring, and model-browse are out (fence).
  - [x] Preserve every 3.3 behavior: mono-tnum numeric display (now in `NumInput`), the model preview + box-placeholder fallback, ◀/▶ browse, `J` toggle, Edit-only visibility, tooltips.
- [x] **Task 3 — Simple/Advanced disclosure + advanced fields + raw-JSON hatch (AC3; D-3/D-5)**
  - [x] Add `ChimeraTabs.Create(TabsVariant.Segment, "Simple", "Advanced")` at the top of the card; `TabChanged` flips a built-once advanced-fields subtree `Visible` (mirror the AbilityEditor `SwitchMode` visibility flip, `AbilityEditorPanel.cs:280-287`; re-entry guard so re-clicking the active tab doesn't rebuild/clobber).
  - [x] Advanced fields (all bound like Task 2): `armor`/`train_time`/`splash_radius`/`collision_radius`/`mesh_scale`/`max_energy` (`NumInput`), `separation_priority` (`Select`), and `prerequisites`/`tags`/`attack_domains` (comma-separated `Input`, parsed to `string[]`). `combat_feedback` is authored via the raw-JSON hatch only (fence).
  - [x] Raw-JSON hatch: a multiline `TextEdit` `_jsonPane` over the **current unit's** JSON object (D-5). `_paneDirty`/`_suppressPaneDirty`/`SetPaneText`; "don't clobber on reveal" (`if (visible && !_paneDirty) ShowJson()`); on Save, dirty pane wins (validate then fold back into the form/model). [`AbilityEditorPanel.Advanced.cs:91-115,168-195`]
- [x] **Task 4 — Godot-free `UnitDefinitionValidator` + Tier-1 tests (AC2, AC5; D-9)**
  - [x] Create `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — Godot-free, no `using Godot`, under a `SimSources.props`-globbed path so Tier-1 compiles it. `Validate(UnitDefinition def, AbilityRegistry registry, IReadOnlyList<UnitDefinition> siblings)` → a `UnitValidationResult` carrying `bool Ok` + `IReadOnlyList<(string FieldPath, string Message)> Errors` (ALL errors, not first-fail — D-9). Located message idiom mirrors `AbilityValidator.Located` (`AbilityValidator.cs:227-228`). Do NOT mint `Validated<UnitDefinition>`.
  - [x] Rules (source-of-truth cited in Dev Notes): `id` non-empty & `SanitizeId(id)==id` & **unique among siblings**; `category` ∈ `UnitCategory` set; `damage_type` ∈ `{Normal,Pierce,Siege,Magic,Hero}`; `armor_type` ∈ `{Unarmored,Light,Medium,Heavy,Fortified,Hero}`; `separation_priority` ∈ its enum set; every numeric stat **finite & `[0, 32768)`** (the 16.16 `Fixed` ceiling — a stat ≥32768 overflows the single `float→Fixed` boundary at spawn); `cost_ore`/`cost_crystal` **≥ 0** (a negative cost *adds* resource each train — the parked 1.3b/2.9b defect); every `abilities[]` id resolves via `registry.IndexOf(id) >= 0`; `tags[]` ∈ the `UnitTag` closed set (compose `UnitTagValidator`).
  - [x] `UnitDefinitionValidatorTests` (Tier-1, Godot-free): a valid def passes with 0 errors; each rule has a RED case (negative cost, stat ≥32768, unknown category/damage/armor, duplicate id, empty id, non-sanitized id, undefined ability ref, unknown tag) asserting the located field path + message; multi-error case returns >1 error.
- [x] **Task 5 — Located error badges (UX-DR55) + fail-closed Save/Playtest gate (AC2; D-4/D-11)**
  - [x] Add the reusable badge helper (D-4): `ChimeraValidationBadge` anchored to a field, `Danger` pill + `ChimeraTooltip(Field)` sentence; `Ok` "valid" state; a clear/hide. Log the new primitive per UX-DR33 in `deferred-work.md` / the story.
  - [x] On Save/Playtest attempt: run the validator (+ the presentation `ResourceLoader.Exists` mesh check, D-9), map each `(FieldPath, Message)` to a badge on the corresponding control, and **block** the action (nothing written / Play denied) when any error exists. All-valid → clear badges, proceed. Mirror the AbilityEditor's `if (!r.Ok) { ShowError; return; }` fail-closed shape (`AbilityEditorPanel.cs:526-527`). Gate the Edit→Play transition per D-11 (only while the card is open with invalid fields).
- [x] **Task 6 — Persistence: JSON-DOM write-back + list ops (AC1, AC4; D-1/D-7/D-10)**
  - [x] Add the faction save helper as **two layers** so the core is Tier-1-testable (the Godot-free test project can't resolve `res://` paths or touch Godot): (a) a **pure string transform** `PatchFactionJson(string factionJson, UnitEdit edit) → string` in `src/Core/Definitions` — `JsonNode.Parse(factionJson)` → `root["units"]` `JsonArray` → find/append/clone/remove the target `JsonObject` by `id`, set only changed properties (omit defaulted optionals to avoid ballooning), return `root.ToJsonString(WriteIndented)`; (b) a thin presentation wrapper that does `File.ReadAllText(abs)` → `PatchFactionJson` → atomic `.tmp`→`File.Move(overwrite:true)` + re-parse self-check (reuse `AbilityEditorPanel.cs:553-580` shape), with `res://`→abs via `ProjectSettings.GlobalizePath` at the call site. AC4's round-trip test targets layer (a) directly with an in-code JSON string.
  - [x] Wire Create/Duplicate/Delete to also mutate the in-memory `_faction.Units` (D-10) and push `EditorHistory` entries (Task 7), then `Refresh()` + re-index the browse cursor.
  - [x] Status line: "saved — applies on next playtest/match" (D-10). Optional Save & Reload button → `GetTree().ReloadCurrentScene()`.
- [x] **Task 7 — Undo/redo via `EditorHistory` (AC1; D-6)**
  - [x] `private readonly EditorHistory _history = new();`. Per edit: capture old+new, `Push(redo: apply-new + Refresh, undo: apply-old + Refresh)`. Create: `Push(redo: add + Refresh, undo: removeAt + Refresh)`; Duplicate: same with the clone; Delete: capture the unit + its list index, `Push(redo: removeAt(i) + Refresh, undo: Insert(i, u) + Refresh)`.
  - [x] Ctrl+Z → `_history.Undo()`, Ctrl+Y → `_history.Redo()`, handled in the panel **only when `_panel.Visible`**, and `GetViewport().SetInputAsHandled()` so `EntityPlacer`'s Ctrl+Z (`EntityPlacer.cs:202-216`) doesn't also fire.
- [x] **Task 8 — Toolbar: Save / Create / Duplicate / Delete + confirm-delete dialog (AC1; D-7)**
  - [x] Add a toolbar row (in the title row or a footer): `Button("Save", Primary)`, `Button("New", Secondary)`, `Button("Duplicate", Ghost)`, `Button("Delete", Danger)`. Delete opens `ChimeraDialog.Create("Delete unit?", "…").AddConfirm("Delete", danger:true)` / `AddCancel("Cancel")` → `Confirmed()` removes (Task 6). Disable Save while invalid (D-11) and disable Delete when the faction has ≤0 units.
- [x] **Task 9 — Tooltips on every control (AC3; UX-DR53)**
  - [x] `ChimeraTooltip.Attach(ctrl, term, body, TooltipRole.Field)` on **every** input, dropdown, toolbar button, and the segment — bold term + one plain "teach never scold" sentence (EXPERIENCE.md:57). On each target set **both** `MouseFilter = Stop` **and** `FocusMode = All` (the keyboard-focus half of AC3/NFR-2 is silently dead otherwise — the 3.3 lesson). Reuse the 3.3 `AttachTip`/`MakeChildrenMouseIgnore` helper for composites.
- [x] **Task 10 — `/godot-verify` + determinism/regression gate (AC4, AC5, AC6)**
  - [x] Reuse/extend the `_unitcard_sample.json` fixture for the DOM-patch round-trip test (a faction with a `signature_mechanic`-style unknown key + a `combat_feedback` unit + multiple units → assert only the edited unit's changed property differs, unknown keys + other units preserved).
  - [x] Build `godot.csproj` (0-err). In-engine: exercise AC6 (edit+Save→file changes; New/Duplicate/Delete; invalid edit → badge + blocked Save; Advanced reveal + raw pane; tooltips on hover + keyboard focus). Capture screenshots.
  - [x] Confirm presentation/authoring-only: Tier-1 green (+ new validator/round-trip tests), **all 18 goldens byte-identical**, stamps **9/3/1/2 + StartStateHash 1** untouched, release analyzer gate 0-err / RS0030 zero-baseline held.

## Dev Notes

### Persistence recipe — the JSON-DOM patch (READ FIRST; this is the story's highest risk)

**Why not just re-serialize the `FactionDefinition`?** A whole-object reflection re-serialize (`JsonSerializer.Serialize(factionDef, …)`) corrupts the file **eight** ways (all verified against `UnitDefinition.cs` + the on-disk faction files):

| # | Corruption | Root cause |
|---|---|---|
| A | Emits six computed getters (`ParsedDamageType`/`ParsedArmorType`/`ParsedSeparationPriority`/`ParsedCategory`/`ParsedAttackDomains`/`ParsedTags`) as **PascalCase integer** fields | get-only props with **no `[JsonIgnore]`**; STJ serializes read-only props; no enum converter → ints. The source comment at **`UnitDefinition.cs:342-344`** states the design assumption: *"the lenient faction loader never re-serializes it, so no `[JsonIgnore]` is needed."* A whole-object re-serialize breaks that invariant. |
| B | **Drops** faction-level `signature_mechanic` (both factions) + `deferred_mechanics` (beta) | not properties on `FactionDefinition`, no `[JsonExtensionData]`, `UnmappedMemberHandling` defaults to `Skip` |
| C | Balloons every unit with all default/omitted fields (`armor:0, splash_radius:0, collision_radius:1, prerequisites:[], attack_domains:null, tags:null, is_hero:false, combat_feedback:null, max_energy:0…`) | no `DefaultIgnoreCondition` |
| D | Writes `null` for unset `mesh_path`/`attack_domains`/`tags`/`combat_feedback` | not `WhenWritingNull` |
| E | Field order changes | STJ emits in declaration order ≠ source order |
| F | Formatting collapses to one line | `FactionDefinition.JsonOptions` has **no `WriteIndented`** |
| G | Number reformat (`4.0`→`4`) | float writer normalizes |
| H | Rewrites all other units + all buildings even for a one-field edit | whole-container serialize |

**The safe write (D-1):**
```csharp
using System.Text.Json.Nodes;
// absPath = ProjectSettings.GlobalizePath(factionResPath) done at the presentation call site.
JsonNode root = JsonNode.Parse(File.ReadAllText(absPath))!;   // preserves untouched tokens verbatim
JsonArray units = root["units"]!.AsArray();
// find by id:
JsonObject? target = units.FirstOrDefault(n => (string?)n?["id"] == edited.Id)?.AsObject();
// EDIT: set only changed props, e.g. target["hp"] = edited.Hp;  target["cost_crystal"] = edited.CostCrystal;
// CREATE: units.Add(BuildUnitNode(edited));   // only the fields the creator set
// DUPLICATE: units.Add(JsonNode.Parse(target!.ToJsonString())!);  then set the new id
// DELETE: units.RemoveAt(indexOfTarget);
string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
// atomic + self-check, mirroring AbilityEditorPanel.WriteFile (:553-580):
File.WriteAllText(absPath + ".tmp", json);
_ = FactionDefinition.LoadFromFile(absPath + ".tmp");   // round-trip self-check: refuse if it won't reload
File.Move(absPath + ".tmp", absPath, overwrite: true);
```
There is **no existing `JsonNode` usage in the codebase** — 3.4 introduces it (a standard `System.Text.Json.Nodes` API; no new NuGet). This preserves faction metadata, every other unit/building byte-stable, un-mutated number tokens verbatim, no computed getters, no ballooning — satisfying AC4. **`res://` is writable at dev time** (globalizes to the project folder, exactly how the AbilityEditor writes `res://resources/data/abilities/`); it's read-only in an exported build, so this is a dev/Creation-Suite-only capability (accepted, same limit the AbilityEditor ships with). [Source: `UnitDefinition.cs:269-346`; `FactionDefinition.cs:99-115`; `alpha_faction.json`/`beta_faction.json`; `AbilityEditorPanel.cs:553-580`]

> **Do NOT use `FactionDefinition.JsonOptions` to write** (read-tuned: no `WriteIndented`, no converters — `FactionDefinition.cs:99-103`). **Do NOT use `ContentJson.Options`** (strict ability path, `UnmappedMemberHandling.Disallow` → would hard-reject `signature_mechanic` on the next load — `ContentJson.cs:26-47`).

### The editable `UnitDefinition` field map (`godot/src/Core/Definitions/UnitDefinition.cs`)

All values are plain `float`/`int`/`string`/`bool`/`string[]` (authoring POCO; `Fixed` conversion happens once at spawn in `EntityWorld.ApplyUnitDefinition`). **No `Fixed`, no enums-as-fields** (enums are string-backed via `Parsed*` getters). Bind `NumInput` straight to the float/int props.

| Field | Property | JSON key | Type / default | Line | Mode |
|---|---|---|---|---|---|
| Id | `Id` | `id` | string / "" | 14 | simple (validated: unique, sanitized, non-empty) |
| Name | `DisplayName` | `display_name` | string / "" | 17 | simple |
| Archetype | `Category` | `category` | string / "Melee" | 21 | simple (Select: Worker/Melee/Ranged/Siege/Air/Structure) |
| Model | `MeshPath` | `mesh_path` | string? / null | 28 | simple (text field + `ResourceLoader.Exists` validation; browse is 3.5) |
| HP | `Hp` | `hp` | float / 100 | 31 | simple |
| Speed | `Speed` | `speed` | float / 4 | 34 | simple |
| Attack | `AttackDamage` | `attack_damage` | float / 10 | 37 | simple |
| Range | `AttackRange` | `attack_range` | float / 5 | 40 | simple |
| Atk interval | `AttackSpeed` | `attack_speed` | float / 1 (sec between attacks) | 44 | simple |
| Damage type | `DamageType` | `damage_type` | string / "Normal" | 48 | simple (Select: Normal/Pierce/Siege/Magic/Hero) |
| Armor type | `ArmorType` | `armor_type` | string / "Unarmored" | 52 | simple (Select: Unarmored/Light/Medium/Heavy/Fortified/Hero) |
| Cost ore | `CostOre` | `cost_ore` | int / 50 | 63 | simple (≥0) |
| Cost crystal | `CostCrystal` | `cost_crystal` | int / 0 | 67 | simple (≥0) |
| Supply | `Supply` | `supply` | int / 1 | 71 | simple |
| Vision | `VisionRange` | `vision_range` | float / 8 | 83 | simple |
| Flat armor | `Armor` | `armor` | float / 0 | 59 | **advanced** |
| Train time | `TrainTime` | `train_time` | float / 8 | 79 | **advanced** |
| Splash | `SplashRadius` | `splash_radius` | float / 0 | 90 | **advanced** |
| Collision | `CollisionRadius` | `collision_radius` | float / 1 | 100 | **advanced** |
| Mesh scale | `MeshScale` | `mesh_scale` | float / 1 | 75 | **advanced** (live re-render is 3.5) |
| Max energy | `MaxEnergy` | `max_energy` | float / 0 | 181 | **advanced** |
| Sep priority | `SeparationPriority` | `separation_priority` | string / "Normal" | 108 | **advanced** (Select) |
| Prereqs | `Prerequisites` | `prerequisites` | string[] / [] | 117 | **advanced** (comma list) |
| Tags | `Tags` | `tags` | string[]? / null | 155 | **advanced** (comma list; `UnitTag` closed set) |
| Attack domains | `AttackDomains` | `attack_domains` | string[]? / null | 138 | **advanced** (comma list) |
| Combat feedback | `CombatFeedback` | `combat_feedback` | CombatFeedbackProfile? / null | 192 | **raw-JSON only** (dual-path DTO — see below) |
| Abilities | `Abilities` | `abilities` | string[] / [] | 126 | validated (undefined-ref badge); **structured authoring is 3.6** (raw-JSON only in 3.4) |
| Is hero | `IsHero` | `is_hero` | bool / false | 172 | **read-only** (HERO tag; Promote-to-Hero switch is 3.7) |

**`[JsonIgnore]` (never author):** `AbilityIndices`/`AuraAbilityIndex`/`OnHitAbilityIndex`/`SelfPassiveAbilityIndex` (`:200-216`). **Never emit** the `Parsed*` getters (`:269,278,293,301,316,346`). **Dual-path DTO:** `CombatFeedbackProfile` rides both the lenient faction loader AND the strict `AbilityDefinition.CombatFeedback` (`Disallow`) — an edited `combat_feedback` must stay: no enum-typed fields, every sub-field a declared settable auto-prop, `float` not `Fixed` (`CombatFeedbackProfile.cs:23-27`; `AbilityDefinition.cs:92-93`) → author it via the raw hatch, don't build a structured sub-form. [[chimera-dual-path-content-dto-constraint]]

### Validator rules — source-of-truth for "valid" (D-9)

Home a Godot-free `UnitDefinitionValidator` beside the other three validators (`src/Core/Definitions`). Reuse the closed-set/`InSet` and `InRange`/`CheckNonNeg` idioms from `ScenarioValidator.cs:262-309` and the `Located(id, path, reason)` message idiom from `AbilityValidator.cs:227-228`. Return **all** field errors (D-9).

| AC2 rule | Field | Valid iff | Source-of-truth |
|---|---|---|---|
| invalid archetype/category | `category` | ∈ `UnitCategory{Worker,Melee,Ranged,Siege,Air,Structure}` | `UnitCategory.cs:14-22` |
| invalid combat type | `damage_type` / `armor_type` | ∈ `{Normal,Pierce,Siege,Magic,Hero}` / `{Unarmored,Light,Medium,Heavy,Fortified,Hero}` (`Hero` is a reserved placeholder but valid) | `DamageTable.cs:15-23,29-38`; confirmed by the `_unitcard_sample` hero using `damage_type:"Hero"` |
| out-of-range/missing stat | `hp/speed/attack_damage/attack_range/attack_speed/armor/splash_radius/collision_radius/mesh_scale/max_energy/vision_range` (+ `supply`) | finite & `[0, 32768)` | the 16.16 `Fixed` ceiling (`Range=32768f`, `ScenarioValidator.cs:42-46`; `FixedJsonConverter.FixedRangeLimit`); ≥32768 overflows the single `float→Fixed` at spawn (deferred-work #2) |
| cost ≥ 0 | `cost_ore` / `cost_crystal` | `>= 0` | a negative cost **adds** resource each train (`BuildingSystem.TrainUnit`; deferred-work.md #1 / epic-2-retro D-2) |
| missing/invalid model path | `mesh_path` | null/empty (→ box placeholder, OK) **or** `ResourceLoader.Exists(path)` | **presentation-side** check (needs Godot); `MeshLoader.cs:21-43` |
| undefined ability reference | each `abilities[]` id | `registry.IndexOf(id) >= 0` | `AbilityRegistry.cs:56-61` |
| unknown tag | each `tags[]` | ∈ the `UnitTag` closed set | compose `UnitTagValidator` (`UnitTagValidator.cs:27-37`) |
| id | `id` | non-empty & `SanitizeId(id)==id` & unique among sibling `Units` | `SanitizeId` (`AbilityEditorPanel.cs:671`); index is load-bearing (`FactionDefinition.cs:49-54`) |

> **Validate values/ranges, not just shape** — the standing content-validator rule ([[chimera-content-validator-bound-behavioral-params]]). This is exactly the parked "unit-cost/start-resource bounds validator" the Epic-2 retro homed here: *"the Unit Card Editor is exactly where a creator authors a bad cost, so the located error lands where they can see and fix it"* (epic-2-retro-2026-07-05.md D-2).

### Editor mechanics — reuse the AbilityEditor patterns, the 3.1 kit for widgets

`EditorHistory` and the AbilityEditor are **disjoint** reuse veins: `EditorHistory` is used only by `EntityPlacer` today (the AbilityEditor has no undo); the AbilityEditor is the template for disclosure/raw-JSON/validate-gated save. Build widgets from the **3.1 kit** (the AbilityEditor's private row-builders predate the Theme and hardcode a house palette — copy the *shapes*, not the code).

| 3.4 need | Reuse | Cite |
|---|---|---|
| Undo/redo class | `EditorHistory` as-is (own instance) | `EditorHistory.cs:27,34,43`; patterns `EntityPlacer.cs:408-428,1045-1074,202-216` |
| Simple/Advanced disclosure | pattern: segment + visibility flip + re-entry guard | `ChimeraTabs(Segment)`; `AbilityEditorPanel.cs:234-315` |
| Raw-JSON hatch | multiline `TextEdit` + dirty guards | `AbilityEditorPanel.Advanced.cs:91-115,168-195` |
| Save (atomic + self-check + overwrite confirm) | pattern, not code (target differs — DOM patch) | `AbilityEditorPanel.cs:553-594` |
| `SanitizeId` + block-save id guard | lift directly | `AbilityEditorPanel.cs:671-678,538-546` |
| Text/number/enum inputs | `ChimeraComponents.Input` / `NumInput` / `Select` | kit inventory below |
| Save/New/Dup/**Delete(Danger)** buttons | `ChimeraComponents.Button(…, ButtonVariant.Danger)` | kit |
| Confirm-delete | `ChimeraDialog.Create(…).AddConfirm("Delete", danger:true)/AddCancel` | `ChimeraDialog` |
| Per-field tooltip | `ChimeraTooltip.Attach(ctrl, term, body, Field)` + `MouseFilter=Stop`+`FocusMode=All` | `ChimeraTooltip`; 3-3 `AttachTip` |
| Located badge (UX-DR55) | **net-new** `ChimeraValidationBadge` = `Tag(Danger)`+`Tooltip` (log per UX-DR33) | GAP — see D-4 |

**Kit component inventory (all require `ChimeraComponents.Initialize` — already done by the 3.3 panel; do NOT re-add):**
- `Input(string placeholder="", string text="")` → `LineEdit` — text fields.
- `Select(params string[] items)` → `OptionButton` — enum dropdowns.
- `NumInput(double value=0, min=0, max=100, step=1)` → `SpinBox` (mono-tnum) — numeric fields; **set `min`/`max`/`step` per field** (e.g. costs `min:0,max:32767,step:1`; float stats `step:0.1` or `0.05`).
- `Button(text, ButtonVariant{Primary,Secondary,Ghost,Danger}, ButtonSize)` ; `IconButton(glyph, isActive, disabled)`.
- `FieldLabel(text)`, `Panel(PanelVariant)`, `Tag(text, TagVariant{Neutral,Lock,Ok,Accent,Danger})`.
- `ChimeraTabs.Create(TabsVariant.Segment, "Simple", "Advanced")` : signal `TabChanged(int)`, `.Active`.
- `ChimeraDialog.Create(title, body)` : `AddConfirm(text, danger)`, `AddCancel(text)`, `Open(parent)`, signals `Confirmed()`/`Dismissed()`.
- `ChimeraTooltip.Attach(ctrl, term, body, TooltipRole{Pop,Field})`.
- (`ChimeraSwitch` = boolean reveal — reserved for 3.7; `ChimeraMark` = decorative Seal, NOT a badge; `ChimeraToastHost` = transient, not the per-field badge.)

[Source: `ChimeraComponents.Controls.cs`/`.Surfaces.cs`; `ChimeraSwitch.cs`; `ChimeraTabs.cs`; `ChimeraDialog.cs`; `ChimeraTooltip.cs`; Agent-5 recon]

### Traps the AbilityEditor already hit that 3.4 will re-hit
1. **Undo mutates the model but not the built-once form** — every history redo/undo must also `Refresh()` (D-6). EntityPlacer escapes this only because presentation polls sim arrays each frame.
2. **Two `EditorHistory` on Ctrl+Z** — gate 3.4's handler on `_panel.Visible` + `SetInputAsHandled()` (`EntityPlacer.cs:202-216`).
3. **Round-trip clobber** — serialize the *current* model in `ShowJson`, never a recomputed default (`AbilityEditorPanel.cs:463-466`).
4. **Re-expand / re-entry clobbers unsaved edits** — `if (visible && !_paneDirty) ShowJson()`; re-clicking the active tab early-returns.
5. **Save that won't reload** — re-parse the serialized faction before reporting "Saved" (fail-closed) — the DOM self-check above.
6. **Instance-aliasing** — `_ctx.FactionDef` (card) may differ from the live match's `_ctx.SlotFactionDefs[…]` (`ScenarioLoadPhase.cs:101`); persist to file + treat as apply-next-match (D-10).
7. **`unit_id` rename/delete is a cross-reference concern** — a scenario referencing the old id (`ScenarioUnit.UnitId`, e.g. `alpha_map_01` spawns `"worker"`) would spawn nothing; not a checksum risk, but keep the ids scenarios reference. **Keep `alpha_faction.json`/`beta_faction.json` parseable** — `CanonicalScenarioTests.cs:59` reads them at test time.

### Determinism / regression posture (AC5)
Pure authoring-time. No sim array, store, system, checksum, or golden changes. No new phase → `PhaseOrderTest` unchanged. New `src/Core` files are Godot-free + analyzer-clean (`src/Core/Definitions` is the authoring-POCO float boundary the analyzer already exempts; the DOM/`System.Text.Json.Nodes` API is not a banned type). Confirm: Tier-1 green (+ new tests), **18 goldens byte-identical**, stamps **9/3/1/2 + StartStateHash 1**, release gate 0-err / RS0030 zero-baseline. Serialization reformat of the faction file is git-diff noise but determinism-harmless (no faction-file byte hash is pinned — the retired algo-1 file-FNV is gone; `CanonicalModelHash` folds the model, not bytes). [Source: `CanonicalModelHash.cs:40-102`; `GoldenApplierScenario.cs:68-86`; `MultiFactionScenario.cs:60-153`; `CanonicalScenarioTests.cs:29-60`; [[chimera-checksum-fold-timing-rule]]]

### Project Structure Notes
- **New files:** `godot/src/Core/Definitions/UnitDefinitionValidator.cs` (Godot-free; + a `UnitValidationResult`), a faction-save helper (either on `FactionDefinition` as `SaveUnit(...)` or a new `FactionWriter.cs` in `src/Core/Definitions`; the DOM logic is Godot-free — the presentation only supplies the globalized path + the `ResourceLoader.Exists` mesh check), `godot/src/UI/Components/ChimeraValidationBadge.cs` (the UX-DR55 kit helper), and Tier-1 tests under `godot/ProjectChimera.Sim.Tests/Definitions/` (`UnitDefinitionValidatorTests`, `FactionWriteRoundTripTests`).
- **Edited files:** `UnitCardPanel.cs` (edit surface + toolbar + validation + undo + save), `UnitCardPhase.cs` (thread the faction path), possibly `UnitCardText.cs` (Godot-free message helpers if any formatting is shared). Reuse the existing `_unitcard_sample.json` fixture (extend for the round-trip test).
- Conventions: `PascalCase.cs`; `#nullable enable`; Godot-inheriting classes `partial`; editor code in `CreationSuite/`, shared kit in `UI/Components/`, Godot-free logic in `src/Core/Definitions/`. [Source: project-context.md:131-135; godot/CLAUDE.md]

### Project Context Rules (from project-context.md — apply to this story)
- **Sim/Presentation boundary is sacred.** The validator + save helper read/write a content POCO + a JSON file; they touch no `EntityWorld`/store/sim array and mutate no sim truth. [project-context.md:75-81]
- **No `Fixed` in the edit path** — unit stats are authoring floats; the sim quantizes once at spawn. Bind `NumInput` to floats directly. [project-context.md:86-87]
- **Data-driven / progressive disclosure** — 3.4 IS the "no gameplay value hardcoded in a path a creator can't reach" story for units, with a simple mode + advanced/raw-JSON mode (the layered-complexity rule). [project-context.md:97-101]
- **Reuse existing systems** (`EditorHistory`, the 3.1 kit, `FactionDefinition.LoadFromFile`, `AbilityRegistry`, the AbilityEditor patterns) rather than building parallel ones. [project-context.md:93-95]
- **Fail-closed content validation** — every shareable construct is statically validatable; the AR-39 gate blocks bad content before save/playtest (NFR-6). [project-context.md:46-49; epics.md:152]

### References
- Requirements & fence: `epics.md:1255-1271` (Story 3.4), `:1273-1323` (3.5/3.6/3.7 downstream reuse of the badge/raw-JSON foundation), `:61-67` (FR-1/2/6/7), `:147-148` (NFR-1/2), `:180` (AR-3), `:322-324` (UX-DR53/54/55), `:348` (UX-DR77), `:296` (UX-DR33). AR-39 = the fail-closed pre-tick gate (`epics.md:689,691`; Story 1.7).
- 3.3 predecessor (extend this): `3-3-read-only-unit-card-panel-...md` (field map :155-178, region blueprint :197-206, kit catalog :141-153, tooltip/`FocusMode.All` lesson :96); `UnitCardPanel.cs` (`Initialize` :81-90, `LoadFactionFromPath` :99-112, `Bind`/`Refresh`/`Browse` :300-331, `BuildBody` :383-454, `AttachTip` :564-579); `UnitCardPhase.cs:21-30`; `UnitCardText.cs`.
- Data model + persistence: `UnitDefinition.cs` (field map above; landmine :342-344; getters :269/278/293/301/316/346); `FactionDefinition.cs` (`JsonOptions` :99-103, `LoadFromFile` :110-115, `Units` :26, `GetUnit`/`IndexOfUnit` :42-60); `ScenarioData.cs:28-30,160-161`; `CombatFeedbackProfile.cs:23-27`; `ContentJson.cs:26-47`; on-disk `alpha_faction.json`/`beta_faction.json`; fixture `_unitcard_sample.json`.
- Validation: `AbilityValidator.cs:227-228` (Located), `Validated.cs:7-31,39-80` (shape + sole-minter), `ScenarioValidator.cs:35,262-309` (Proof, InRange/CheckNonNeg/InSet), `UnitTagValidator.cs:8-9,27-37`, `AbilityValidationResult.cs:12-39`; enums `UnitCategory.cs:14-22`, `DamageTable.cs:15-38`; `AbilityRegistry.cs:56-61`. Parked defect: `deferred-work.md` #1/#2; `epic-2-retro-2026-07-05.md` D-2.
- Editor mechanics: `EditorHistory.cs:20-52`; `EntityPlacer.cs:202-216,408-428,1045-1074`; `AbilityEditorPanel.cs:234-315,459-594,636-678` + `.Advanced.cs:91-195`; kit `ChimeraComponents.Controls.cs`/`.Surfaces.cs`, `ChimeraTabs.cs`, `ChimeraDialog.cs`, `ChimeraTooltip.cs`, `ChimeraSwitch.cs`.
- Wiring + determinism: `MainScene.cs:182-183,246-254,505-506,534-536`; `ScenarioLoadPhase.cs:92-115`; `FactionRegistry.cs:18-26`; `CanonicalModelHash.cs:40-102`; `GoldenApplierScenario.cs:68-86`; `CanonicalScenarioTests.cs:29-60`; `SimChecksum.cs:88`.
- UX/arch: `ux-Project_Chimera-2026-06-20/EXPERIENCE.md:46,57,62-63,100,145,156`; `DESIGN.md:80-98,188-189`; project-context.md:46-135.
- Baseline: git HEAD `f7a54ef` (2026-07-06). Related memory: [[chimera-checksum-fold-timing-rule]], [[chimera-dual-path-content-dto-constraint]], [[chimera-content-validator-bound-behavioral-params]], [[chimera-godot-theme-authoring-gotchas]], [[chimera-enum-indexed-array-touch-sites]], [[sprint-status-yaml-structure-quirk]].

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (gds-dev-story, 2026-07-06). All 11 recommended-default decisions (D-1…D-11) taken as written.

### Debug Log References

- Tier-1 (godot-free) suite: **761 pass / 1 skip / 0 fail** (716 → 761; +45 new: 26 `UnitDefinitionValidatorTests`, 19 `FactionWriteRoundTripTests` incl. the `SyncFactionUnits` + `combat_feedback` change-detection cases). All 18 goldens byte-identical.
- `godot.csproj` build: **0 errors**.
- Release analyzer gate (`-p:ChimeraRelease=true --no-incremental`): **0 errors**, RS0030 zero-baseline held. My two new sim files add only advisory CHM0001/IL warnings (same classes as `ScenarioValidator`/`LLMService`); the CHM0006 I first introduced was removed by interpolating the float directly (the `ScenarioValidator` idiom).
- `/godot-verify` (Godot 4.6.3, in-engine, driven via godot-mcp signals/exec on the isolated `_unitcard_sample.json` fixture): **PASS** on all six AC6 clauses, **0 error-log messages**, panel boots + runs without exception. Fixture + `alpha_faction.json` restored via `git checkout` after the Save test.

### Completion Notes List

**Built (all 10 tasks).** The 3.3 read-only card is now the Unit Card **Editor** in place (D-2): readouts → `ChimeraComponents` Input/Select/NumInput bound to the live `UnitDefinition`; Simple/Advanced `ChimeraTabs.Segment` disclosure (D-3) revealing advanced fields + a raw-JSON `TextEdit` hatch (D-5, seeded from a new `FactionWriter.SerializeUnitClean`); a Save/New/Duplicate/Delete toolbar with a `ChimeraDialog` danger-confirm on Delete (D-7); fail-closed validation with per-field `ChimeraValidationBadge` (the net-new UX-DR55 kit helper, D-4) + the presentation `ResourceLoader.Exists` mesh check (D-9); undo/redo via a private `EditorHistory` gated on visibility + `SetInputAsHandled()` (D-6); and write-back to the faction JSON (D-1/D-10).

**Persistence model (the story's highest-risk part).** The Godot-free core is the tested piece: `UnitDefinitionValidator` (returns ALL located `(FieldPath, Message)` errors, not first-fail — D-9; closes the parked 1.3b/2.9b negative-cost + Fixed-≥32768 overflow defects; does NOT mint `Validated<T>`, so the sole-minter allow-list is untouched) and `FactionWriter` (a `System.Text.Json.Nodes` DOM patch — untouched value tokens are JsonElement-backed so they re-serialize verbatim; only changed fields are set; `combat_feedback` is preserved verbatim unless a raw-hatch edit actually changed it). **Decision (documented for review):** the panel keeps ALL edits (field + Create/Duplicate/Delete) in memory with an in-memory undo stack, and **Save** is the single persistence action — it reconciles the whole in-memory unit list into the file via **`FactionWriter.SyncFactionUnits`** (the whole-list generalization of `PatchFactionJson`), fully honoring D-6 ("undo in-memory, Save persists") and AC4 (untouched units/buildings/faction-keys byte-identical). `PatchFactionJson` (single-edit, D-1's literal recipe) is retained + tested as the shared-`ApplyFields` primitive.

**Determinism (AC5) — PURE AUTHORING-TIME, ZERO FOLD.** No sim array/store/checksum/golden touched; no new phase (`PhaseOrderTest` untouched); stamps stay **9 / 3 / 1 / 2 + StartStateHash 1**; all **18 goldens byte-identical**. The only `src/Core` additions are the Godot-free `UnitDefinitionValidator.cs` + `FactionWriter.cs` (analyzer-clean, 0 release errors).

**Verify caveat honestly noted:** the whole-faction-file re-indent (WriteIndented expands single-line arrays like `color`) is git-diff noise but determinism-harmless — no faction-file byte hash is pinned (the story's determinism section predicted this). Untouched *values* and unknown keys (`_comment`, `signature_mechanic`) are preserved; the Save test confirmed `hp` moved to 999 while `speed:3.5`/`attack_range:7.0` decimals and `_comment` survived.

**UX-DR33 primitive logged:** `ChimeraValidationBadge` is net-new and reused by 3.5/3.6/3.7 — logged in `deferred-work.md`.

**Two small polish gaps (non-blocking, noted for review):** (1) numeric fields commit undo entries on a focus-session (focus-in snapshot → focus-out commit), so a pure-arrow-click tweak with no subsequent focus change persists on Save but is not individually undoable; (2) undo-of-Delete re-inserts at the original in-memory index (order preserved), but a Save→reload cycle is what re-materializes render slots. Panel `_Input` preempts `EntityPlacer`'s Ctrl+Z (panel is the last-added scene node) — verified structurally; the F5 Edit→Play gate blocks only when the current unit is invalid (D-11).

### File List

**New:**
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` (Godot-free; `UnitDefinitionValidator` + `UnitValidationResult` + a shared `SanitizeId`)
- `godot/src/Core/Definitions/FactionWriter.cs` (Godot-free; `UnitEdit`/`UnitEditKind`, `PatchFactionJson`, `SyncFactionUnits`, `SerializeUnitClean`)
- `godot/src/UI/Components/ChimeraValidationBadge.cs` (the UX-DR55 kit helper) + `.cs.uid` (Godot-generated)
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` (the 3.4 edit surface partial)
- `godot/ProjectChimera.Sim.Tests/Definitions/UnitDefinitionValidatorTests.cs` (26 tests)
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` (19 tests)

**Edited:**
- `godot/src/CreationSuite/UnitCardPanel.cs` (shell: edit-state fields, `Initialize(...,factionJsonPath)`, Segment + status + toolbar in `BuildUi`, `Bind`/`Refresh`/`ClearHosts` for the edit surface; 3.3 preview/header/tooltips preserved)
- `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs` (thread the faction `res://` path — scenario slot-0 `faction_json` ?? `MainScene.P1_FACTION_JSON`)
- `godot/src/Core/MainScene.cs` (`P1_FACTION_JSON` `private`→`internal` so the phase can read it)

### Change Log

| Date | Version | Change |
|---|---|---|
| 2026-07-06 | 1.0 | Implemented via `gds-dev-story` [claude-opus-4-8], baseline `f7a54ef`. All 10 tasks + 6 ACs. Godot-free `UnitDefinitionValidator` (all-errors, D-9) + `FactionWriter` (DOM patch + `SyncFactionUnits` whole-list Save + `combat_feedback` change-detection) TDD'd to 45 new Tier-1 tests; `ChimeraValidationBadge` (UX-DR55) kit helper; the 3.3 panel made editable in place (fields/disclosure/raw-JSON/toolbar/undo/validation) with in-memory-edit + Save-syncs model (D-6/D-10). PURE AUTHORING-TIME zero-fold — stamps 9/3/1/2 + SSH1, 18 goldens byte-identical, release gate 0-err/RS0030-clean. `/godot-verify` PASS (all AC6 in-engine, 0 error-log). Status → review. |
| 2026-07-06 | 0.1 | Story created via `gds-create-story`: 5 parallel live-source recon agents (3.3 panel + persistence + EditorHistory/AbilityEditor + validator/badge + kit/UX/determinism) at baseline `f7a54ef`, lead-verified against source. Headline finding: whole-`FactionDefinition` re-serialize is unsafe (8 corruption modes) → **JSON-DOM targeted patch** (D-1). No unit validator exists → new Godot-free `UnitDefinitionValidator` (D-9, closes the parked 1.3b/2.9b negative-cost defect). UX-DR55 badge is net-new (D-4). Zero-fold confirmed (no golden loads faction files; `CanonicalModelHash` folds path-strings not stats). 11 recommended-default decisions surfaced. Status → ready-for-dev. |
