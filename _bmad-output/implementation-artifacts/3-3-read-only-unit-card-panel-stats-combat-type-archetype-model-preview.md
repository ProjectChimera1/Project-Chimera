# Story 3.3: Read-only Unit Card panel: stats, combat type, archetype, model preview

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a creator,
I want a single consolidated panel that displays an existing unit's stats, archetype, combat type, model, and attached abilities,
so that I can see everything about a unit type in one WC3-style card before I start editing it.

## Context & Scope — read first

This is the **display shell** of the Unit Card Editor (UX-DR77) — the first slice of Epic 3's consolidated "author units with no JSON" editor. It is **READ-ONLY in every sense**: it renders an existing `UnitDefinition` (a content-definition POCO loaded from faction JSON) and **mutates nothing**. Editing, Save, validation, model-browse, ability/archetype authoring, and Promote-to-Hero all arrive in later stories — see the fence below.

Three hard rules for this story:

1. **Build it from the Story-3.1 kit — no new primitives.** The panel composes entirely from `ChimeraComponents` + `ChimeraListRow` + `ChimeraTooltip` styled by `res://assets/ui/main.tres`. Do **not** hardcode a house palette (the nearest precedent, `AbilityEditorPanel.cs`, predates the Theme and hardcodes colors — clone its *lifecycle shape*, never its palette).
2. **Reuse the existing GLB path — don't rebuild it.** The in-panel 3D preview loads the mesh through the existing `MeshLoader.LoadFromGlb` (box-placeholder fallback is built-in). `AssetPreviewScene` is a reference for camera/light/turntable values only.
3. **Add nothing to `UnitDefinition`.** Every field the card shows already exists on the definition (verified). No new definition fields, no editable controls.

**⚠ Integration gotcha that WILL crash if missed (verified):** `ChimeraComponents.Initialize(theme, accent)` is currently called **only** in the two Story-3.1 demo scenes (`ComponentGallery.cs:41`, `ComponentPreview.cs:42`) — **never in `MainScene` or any bootstrap phase**. This panel is therefore the **first in-scene consumer of the kit**, and it MUST initialize the factory itself (load `main.tres` + create an `AccentController` + call `Initialize`) before any `ChimeraComponents.*` call, or every factory method throws `InvalidOperationException`. See Decision D-2 and the Dev Notes recipe.

**Determinism posture — PURE PRESENTATION, zero fold:** the card reads `UnitDefinition` (a content POCO, even further from sim than a live entity) and touches **no** `EntityWorld`/`BuildingStore`/`HeroStore`/sim array. It moves **no** golden and **no** checksum. Stamps stay **9 / 3 / 1 / 2 + StartStateHash 1**; all 17 goldens byte-identical; nothing in `src/Core`, `src/Combat`, `src/Economy`, `src/Navigation` changes. This is the 3.1c posture, not a sim story. [Source: _bmad-output/project-context.md:75-81; [[chimera-checksum-fold-timing-rule]]]

### Scope fence (explicitly OUT of 3.3 — do not build)

| Deferred capability | Owner story | Source |
|---|---|---|
| Edit / Create / Duplicate / Delete fields; Save to faction JSON | 3.4 | epics.md:1255-1271 |
| Inline validation, located error badges (UX-DR55) | 3.4 / 3.6 | epics.md:1265 |
| Simple/Advanced disclosure switch (UX-DR54); raw-JSON hatch | 3.4 | epics.md:1267 |
| Model **browse** / **live-assign** / explicit "box placeholder" *choice* | 3.5 | epics.md:1273-1287 |
| Archetype + ability/behavior **composition authoring**; new def fields | 3.6 | epics.md:1289-1305 |
| Promote-to-Hero switch, leveling curve, XP, signature/ultimate slots | 3.7 | epics.md:1307-1323 |

## Acceptance Criteria

**AC1 — the consolidated read-only card** *(epics.md:1247)*
**Given** a faction JSON with existing `UnitDefinition`s **When** I open the Unit Card panel for a unit **Then** one panel (built from the 3.1 component kit) shows, in a single view (UX-DR77 layout, read-only):
- **stats**: hp, speed, attack (damage), range, attack-speed, cost, supply, vision;
- **combat type**: `damage_type` + `armor_type`;
- **archetype/category**;
- **model reference**;
- **any attached ability list**;

**And** every numeric field is rendered with the **UX-DR34 mono tabular readout** component (`ChimeraComponents.Readout` / mono_tnum font — no proportional-font numbers).

**AC2 — the in-panel 3D preview + safe fallback** *(epics.md:1249)*
**Given** a unit whose `mesh_path` resolves to a GLB **When** the card is shown **Then** the assigned 3D model renders in an in-panel preview viewport **reusing** the existing `AssetPreviewScene`/`MeshLoader` path (FR-3 display half) **And** a unit with **null/missing `mesh_path` shows the box placeholder instead of failing** (no crash, other fields still render).

**Covers:** FR-2 (display half), FR-3 (display half), UX-DR77 (layout), UX-DR34 (numeric readout), UX-DR53 (tooltips). **Depends on:** 3.1c. [Source: epics.md:1251]

### Additional acceptance (derived from the "Covers" requirements + baked decisions)

- **AC3 (UX-DR53 / NFR-2):** every field/label/control carries a hover-**and**-keyboard-focus tooltip via `ChimeraTooltip.Attach`, following the EXPERIENCE.md microcopy pattern (bold term + one plain sentence). [epics.md:322; EXPERIENCE.md:57,156]
- **AC4 (kit-first, no new primitives):** no color/size that exists as a Theme token is hardcoded; the panel renders correctly styled (chamfered surfaces, mono numbers, accent) with the factory initialized. [epics.md:1197; UX-DR33 epics.md:296]
- **AC5 (determinism/regression):** Tier-1 suite green (incl. `PhaseOrderTest`), full `godot.csproj` build 0-err, all goldens byte-identical, stamps 9/3/1/2 + StartStateHash 1 untouched (presentation-only change).
- **AC6 (in-engine, `/godot-verify`):** the panel opens in-engine, shows AC1's full field set from the kit for a real alpha unit, renders its GLB in the preview, correctly falls back to the box placeholder for a null-mesh unit, and shows tooltips on hover.

## Decisions (recommended defaults — confirm with Alec)

All baked into the Tasks/ACs below as the **recommended default**; flip any before or during dev.

- **D-1 — Entry point & data source (the one real scoping call).** No unit-selection→panel host exists yet (the real select flow is 3.4+). **Default: ship a self-contained display harness** — an Edit-mode key toggle on **`J`** (verified free), mirroring the AbilityEditor's `K`, that opens the panel bound to the current scenario's **first faction** (`_ctx.FactionDef.Units`) with **◀/▶ prev-next** to cycle that faction's units; plus a standalone `/godot-verify` path fed a sample faction so AC1/AC2 are demonstrable without a browser. *Alt: wait for a roster/browser UI (blocks 3.3 on 3.4/3.6).* Rationale: keeps 3.3 display-only and self-contained; 3.4 replaces the toggle with the real select flow.
- **D-2 — Kit initialization ownership.** Verified: nothing in `MainScene` initializes the kit today. **Default: the panel self-initializes the kit** (load `main.tres` via `ThemeBuilder.ThemePath` with `CacheMode.Ignore`, create an `AccentController`, call `ChimeraComponents.Initialize`), **guarded by `ChimeraComponents.IsInitialized`** so it's a no-op if a future startup phase (Story 3.11) already did it. *Alt: add a dedicated `ThemePhase` now (scope creep into 3.11).*
- **D-3 — Ability-list depth.** **Default: resolve** each `def.Abilities` id → `AbilityRegistry.Get(idx).DisplayName` (registry lives on `SceneContext.AbilityRegistry`; **fall back to the raw id** if unresolved or registry is `Empty`), and **include ALL attached abilities** by resolving the raw `Abilities` id list directly — do **not** use the `[JsonIgnore] AbilityIndices` (castable-only; drops passives; empty until scenario link). Rows are **text-only** (no icon field exists anywhere). *Alt: show raw ids.*
- **D-4 — Cost is two fields.** `cost` = `CostOre` (int) + `CostCrystal` (int). **Default: show both** as separate mono readouts (WC3-style), crystal shown even at 0. *Alt: hide crystal when 0.*
- **D-5 — Field set = the AC's closed list only.** **Default: display exactly the AC-named fields** + a `DisplayName`/`Id` header + archetype. Do **not** surface the other real def fields (`armor` flat value, `train_time`, `tags`, `prerequisites`, `max_energy`, `attack_domains`, `splash_radius`, `collision_radius`, `separation_priority`) — they belong to the 3.4/3.6 editing surfaces. *Alt: add obvious neighbours (e.g. flat `armor` pairs with `armor_type`).*
- **D-6 — `is_hero` indicator.** `is_hero` exists on the def (added 3.2) but is not in the AC list. **Default: show a passive, non-interactive "HERO" `Tag`** when `def.IsHero == true` (zero authoring, reflects existing data, reinforces the WC3 card). Promote-to-Hero authoring/leveling stays 3.7. *Alt: omit the badge entirely.*
- **D-7 — Attack-speed presentation.** `attack_speed` is **seconds between attacks** (higher = slower), not attacks/sec. **Default: label it "ATK INTERVAL" with an "s" and a clarifying tooltip** ("seconds between attacks — lower is faster"). *Alt: derive attacks/sec = 1 / attack_speed.*
- **D-8 — Preview framing & motion.** **Default: a slow live turntable** (reuse AssetPreviewScene's ~30°/s + its camera/light values) in an **isolated** SubViewport world (`OwnWorld3D = true`), rendering **only while the panel is visible** (`UpdateMode.Always` when shown, `Disabled` when hidden), plus a **minimal AABB-based camera-distance fit** so large units (Siege/Structure meshes) don't clip the fixed frame. *Alt: fixed camera / static single frame (simpler, but clips big meshes).*
- **D-9 — Read-only ability-row affordance.** `ChimeraListRow` is interactive by default; `SetLocked(true)` dims to 0.6 (reads as "disabled"). **Default: plain rows with no `ListRowGroup` and no selection wiring** — inert but full-opacity. *Alt: `SetLocked(true)`.*
- **D-10 — Units only, not Buildings.** `FactionDefinition.Buildings` reuses the same `UnitDefinition` POCO but has 0 speed/attack/supply + `Structure` category (would render as a broken unit). **Default: browse `FactionDef.Units` only.** *Alt: include Buildings.*

## Tasks / Subtasks

- [ ] **Task 1 — Panel scaffold + kit self-init (AC1, AC4; D-2)**
  - [ ] Create `godot/src/CreationSuite/UnitCardPanel.cs` — `public partial class UnitCardPanel : Node` in namespace `ProjectChimera.CreationSuite`, `#nullable enable`. Own an inner `CanvasLayer { Layer = 11 }` (verified free; 16 also free) + a `PanelContainer` (`SetAnchorsPreset(Control.LayoutPreset.CenterRight)`), mirroring `AbilityEditorPanel.cs:129`.
  - [ ] In `_Ready()` → `EnsureKitInitialized()` then `BuildUi()`. `EnsureKitInitialized()`: `if (ChimeraComponents.IsInitialized) { Theme = <the loaded theme>; return; }` else run the exact ComponentGallery init recipe (load `ThemeBuilder.ThemePath` `CacheMode.Ignore` → `Theme = _theme` → `new AccentController` + `AddChild` + `Initialize(_theme)` → `ChimeraComponents.Initialize(_theme, _accent)`). Declare the theme field as **`Godot.Theme`** (fully-qualified — the `ProjectChimera.UI.Theme` namespace shadows the bare type).
  - [ ] `public void Initialize(ScenarioData? scenario, GameState gameState, AbilityRegistry registry)` (called by the phase after `AddChild`): store deps, subscribe `gameState.ModeChanged += OnModeChanged`, start `_panel.Visible = false`. `Toggle()` flips `Visible` (and refreshes on open); `Close()` sets `false`; `OnModeChanged(int mode)` hides when `mode == (int)GameMode.Play` (Edit-only). Match `AbilityEditorPanel.cs:97-120` (no explicit unsubscribe — panel lives for scene lifetime).
- [ ] **Task 2 — Phase wiring: the test-guarded 3-edit contract + toggle (AC5; D-1)**
  - [ ] Create `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs` — `sealed class UnitCardPhase : ISetupPhase`, `Name => "UnitCard"`, `Run()` news the panel, `_ctx.Scene.AddChild`, `Initialize(_ctx.Scenario, _ctx.GameState, _ctx.AbilityRegistry)`. Clone `AbilityEditorPhase.cs:13`.
  - [ ] Add `public CreationSuite.UnitCardPanel UnitCardPanel = null!;` to `SceneContext.cs` (near :104).
  - [ ] **Add `"UnitCard"` in all three lockstep locations** (miss one → startup `AssertOrder` throw + Tier-1 `PhaseOrderTest` red): (1) `ScenePhaseOrder.Canonical` (append after `"AbilityEditor"`); (2) the `ISetupPhase[]` literal in `MainScene._Ready` (`new UnitCardPhase(_ctx)` at the **same index**, ~:353-378); (3) `ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` expected array.
  - [ ] Add the open trigger in `MainScene._UnhandledInput`, **after** the Edit-mode guard (`if (_ctx.GameState.Mode != GameMode.Edit) return;`, :505): `else if (key.Keycode == Key.J) { _ctx.UnitCardPanel.Toggle(); GetViewport().SetInputAsHandled(); }` (:528 cluster).
- [ ] **Task 3 — Stat block + combat/archetype tags + header (AC1, AC3; D-4/D-5/D-6/D-7)**
  - [ ] Header region: `DisplayName` (display font, `text_hi`) + `Id` (small `text_lo`) + archetype `Tag(def.Category)` + passive `Tag("HERO", Accent)` when `def.IsHero` (D-6).
  - [ ] Combat region: `Tag(def.DamageType)` + `Tag(def.ArmorType)` (uppercase pills) + attack/range/interval readouts.
  - [ ] Numeric readouts via `ChimeraComponents.Readout(iconColor, FormatStat(value), LABEL)` for **hp, speed, attack, range, atk-interval, cost_ore, cost_crystal, supply, vision** — this is the UX-DR34 requirement; no plain Labels for numbers. Group into stats / economy regions per the UX-DR77 blueprint.
  - [ ] Add `FormatStat(float) => v.ToString("0.##", CultureInfo.InvariantCulture)` (trims trailing zeros: `55f`→"55", `0.95f`→"0.95"; invariant culture for consistent display). Ints format directly.
  - [ ] `ChimeraTooltip.Attach(control, term, body, Field)` on every field (AC3); set each target's `MouseFilter = Stop`. Attack-interval tooltip clarifies the seconds-between-attacks semantic (D-7).
- [ ] **Task 4 — Attached ability list (AC1; D-3/D-9)**
  - [ ] For each id in `def.Abilities`: `int idx = registry.IndexOf(id); string label = idx >= 0 ? registry.Get(idx).DisplayName : id;` → inert `ChimeraListRow.Create(label)` (no group, no selection — D-9). Include ALL ids (do not use `AbilityIndices`).
  - [ ] Empty `Abilities` → a single muted "No abilities" row (don't leave the region blank).
- [ ] **Task 5 — In-panel 3D preview + box fallback (AC2; D-8)**
  - [ ] Build the preview host once: `SubViewport { Size, RenderTargetClearMode = Always, RenderTargetUpdateMode = Always, OwnWorld3D = true }` with `Camera3D` + key/fill `DirectionalLight3D` + a `WorldEnvironment` ambient + a turntable `Node3D` as children; wrap in `SubViewportContainer { Stretch = true, StretchShrink = 1 }`; add to the model region. **Do NOT** set `svp.World3D = GetViewport().World3D` (that shares the game world — MinimapBridge does it on purpose; the card needs isolation). Reuse camera/light values from `AssetPreviewScene.cs:133-162`.
  - [ ] On show/unit-switch: `foreach (child in _turntable.GetChildren()) child.QueueFree();` then `var mesh = MeshLoader.LoadFromGlb(def.MeshPath ?? "", new Vector3(0.8f,1.6f,0.8f), tint); var mi = new MeshInstance3D { Mesh = mesh }; mi.Scale = MeshLoader.ScaleFromDefinition(def.MeshScale); _turntable.AddChild(mi);` (mirrors `AssetPreviewScene.ShowUnit` :97-129). Null/missing/failed path → box placeholder automatically (AC2 clause 2).
  - [ ] Minimal AABB fit: read `mesh.GetAabb()`, position/dolly the camera to frame it (D-8). Set the SubViewport `UpdateMode = Disabled` when the panel hides and `Always` when it shows (don't render a 3D frame every tick while closed).
  - [ ] Show the `mesh_path` string (or "— (box placeholder)") as a mono readout next to the preview (the "model reference" AC1 item).
- [ ] **Task 6 — Selection/browse harness (AC1, AC6; D-1/D-10)**
  - [ ] `Bind(UnitDefinition def)` rebuilds all regions for the given def and refreshes the preview. `◀ / ▶` buttons (`ChimeraComponents.IconButton` or `Button`) cycle `_ctx.FactionDef.Units` (Units only — D-10), wrapping. Header shows "unit i of N".
  - [ ] Guard: faction with 0 units → an empty-state row; `_ctx.FactionDef` null → hide gracefully.
- [ ] **Task 7 — Tier-1 tests (Godot-free) for the pure logic (AC5)**
  - [ ] `UnitCardFormatTests`: `FormatStat` cases (55→"55", 0.95→"0.95", 1.5→"1.5", 8→"8"), invariant-culture.
  - [ ] `UnitCardAbilityResolveTests`: the id→DisplayName resolution helper against a stub `AbilityRegistry` — known id→name, unknown id→raw id fallback, passive ids included, empty→empty. (Extract the resolution to a Godot-free static so it's Tier-1-testable; the panel calls it.)
  - [ ] `PhaseOrderTest` update asserted green (Task 2).
- [ ] **Task 8 — `/godot-verify` + regression gate (AC5, AC6)**
  - [ ] Build `godot.csproj` (0 err). Run; enter Edit mode; press `J`; verify AC1 field set renders from the kit with **mono** numbers, combat/archetype tags, ability names, and the 3D preview shows the alpha unit's GLB; cycle to a **null-`mesh_path`** unit → **box placeholder, no crash**; hover a field → tooltip. Capture screenshots.
  - [ ] Confirm the change is presentation-only: Tier-1 suite green, **all 17 goldens byte-identical**, stamps **9/3/1/2 + StartStateHash 1** untouched, release analyzer gate 0-err (CreationSuite is Godot presentation — the sim banned-API analyzer does not apply, but the build must be clean).

## Dev Notes

### The kit-initialization gotcha (READ FIRST)
`main.tres` is **not** the global project theme — `project.godot` has no `[gui]` theme section (verified). And `ChimeraComponents.Initialize` runs today **only** in the two 3.1 demo scenes, not in `MainScene`. So this panel must own the bootstrap. Exact recipe, lifted from `ComponentGallery.cs:34-41`:
```csharp
using GodotTheme = Godot.Theme; // avoid the ProjectChimera.UI.Theme namespace shadow
...
if (!ChimeraComponents.IsInitialized)
{
    _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
             ?? ThemeBuilder.Build();
    _accent = new AccentController { Name = "AccentController" };
    AddChild(_accent);
    _accent.Initialize(_theme);
    ChimeraComponents.Initialize(_theme, _accent);
}
Theme = _theme; // propagates the theme down this panel's Control subtree
```
Guarding on `IsInitialized` makes it forward-safe: when Story 3.11 hoists kit-init to a startup phase, this becomes a no-op. **Never** `ResourceSaver.Save` over `main.tres` (churns resource IDs). [Source: ChimeraComponents.cs:74,81,147-153; ComponentGallery.cs:34-41; ThemeBuilder.cs:26; 3-1c File List:211]

### Component catalog — build the card from these (no new primitives, UX-DR33)
| Need | Factory (all `godot/src/UI/Components/`) | Signature / note |
|---|---|---|
| Panel/section surface | `ChimeraComponents.Panel(variant)` | `PanelVariant` Default/Surface2/Flat/Accent; chamfered, 16px pad. Surfaces.cs:23 |
| **Numeric stat readout (UX-DR34)** | `ChimeraComponents.Readout(Color iconColor, string value, string label)` | HBox: 22px icon plate + **mono-tnum-700** value + uppercase label. **value is a string — format it.** Surfaces.cs:132 |
| Compact count pill | `ChimeraComponents.Chip(string number, string? label)` | mono-tnum. Surfaces.cs:95 |
| Combat-type / archetype pill | `ChimeraComponents.Tag(string text, TagVariant variant = Neutral)` | uppercased; variants Neutral/Ok/Accent/Danger/Lock. Surfaces.cs:169 |
| Small field/section label | `ChimeraComponents.FieldLabel(string text)` | only label factory (uppercase, 11px, text_lo). Controls.cs:258. **No section-header factory** — for a bigger header replicate the gallery-local `Heading` (Label + display font + `t_*` size + text_hi, ComponentGallery.cs:449). |
| Ability list row | `ChimeraListRow.Create(string text, ListRowGroup? group = null)` | `.Content` HBox for extra chips; `SetLocked(bool)` dims to 0.6. Use **no group** for inert read-only rows (D-9). ChimeraListRow.cs:37 |
| Optional region tabs | `ChimeraTabs.Create(TabsVariant, params string[])` | `TabChanged(int)`. ChimeraTabs.cs:37 |
| **Tooltip (UX-DR53)** | `ChimeraTooltip.Attach(Control ctrl, string term, string body, TooltipRole role = Pop)` | hover + keyboard focus; target needs `MouseFilter = Stop`. Roles Pop/Field. ChimeraTooltip.cs:43 |

Same-assembly token access: `ChimeraComponents.Col(token)`, `.Const`, `.FontOf`, `.SizeOf` (internal) or `_theme.GetColor(token, ThemeTokens.Type)`. Tokens (type `"Chimera"`): `surface_0..4`, `line`, `text_hi/mid/lo`, `accent(+bright/dim/ink/glow/wash)`, `ok/warn/danger/info`, fonts `font_display/ui/mono/mono_tnum`, sizes `t_2xs(11)..t_5xl`, consts `s1..s8`, `cut(8)`. **`team_*` are reserved for world units — never UI chrome.** [Source: ChimeraComponents.cs:156-165; ThemeTokens.cs:36-127; main.tres:14-75]

### UnitDefinition field map — what to display (`godot/src/Core/Definitions/UnitDefinition.cs`)
All values are **plain `float`/`int`/`string`** (this is the authoring POCO; `Fixed` conversion happens only at spawn in `EntityWorld.ApplyUnitDefinition`). **No `Fixed`, no sim reads.**

| Card item | Property | JSON key | Type / default | Line |
|---|---|---|---|---|
| Title | `DisplayName` | `display_name` | string / "" | :17 |
| Subtitle | `Id` | `id` | string / "" | :14 |
| Archetype | `Category` (or `ParsedCategory`) | `category` | string / "Melee" → `UnitCategory{Worker,Melee,Ranged,Siege,Air,Structure}` | :20,:300 |
| HP | `Hp` | `hp` | float / 100 | :31 |
| Speed | `Speed` | `speed` | float / 4 | :34 |
| Attack | `AttackDamage` | `attack_damage` | float / 10 | :37 |
| Range | `AttackRange` | `attack_range` | float / 5 | :40 |
| Atk interval | `AttackSpeed` | `attack_speed` | float / 1 — **seconds between attacks (higher=slower)** | :43 |
| Damage type | `DamageType` | `damage_type` | string / "Normal" {Normal,Pierce,Siege,Magic} | :47 |
| Armor type | `ArmorType` | `armor_type` | string / "Unarmored" {Unarmored,Light,Medium,Heavy,Fortified} | :51 |
| Cost (ore) | `CostOre` | `cost_ore` | int / 50 | :63 |
| Cost (crystal) | `CostCrystal` | `cost_crystal` | int / 0 | :67 |
| Supply | `Supply` | `supply` | int / 1 | :70 |
| Vision | `VisionRange` | `vision_range` | float / 8 | :82 |
| Model ref | `MeshPath` (+ `MeshScale`) | `mesh_path` / `mesh_scale` | string? / null ; float / 1 | :24,:74 |
| Abilities | `Abilities` | `abilities` | string[] of **ids** / empty | :120 |
| Hero badge (D-6) | `IsHero` | `is_hero` | bool / false (added 3.2) | :158 |

**Do NOT display** (present on the def but out of the AC's closed set — D-5): `Armor` (flat), `TrainTime`, `SplashRadius`, `CollisionRadius`, `SeparationPriority`, `Prerequisites`, `AttackDomains`, `Tags`, `MaxEnergy`, `CombatFeedback`. **Do NOT use** the `[JsonIgnore]` `AbilityIndices`/`AuraAbilityIndex`/`OnHitAbilityIndex`/`SelfPassiveAbilityIndex` — they're empty until scenario link and drop passives (:195-216). Faction load: `FactionDefinition.LoadFromFile(ProjectSettings.GlobalizePath(resPath))` → `.Units` (List). Note `.Buildings` is *also* `List<UnitDefinition>` — **read `.Units` only** (D-10). Real sample values: `alpha_faction.json:12-32`. [Source: UnitDefinition.cs; FactionDefinition.cs:26,99-115; UnitCategory.cs:14-22; DamageTable.cs:15-38]

### Ability-list recipe (Godot-free, extract for Tier-1)
```csharp
// def.Abilities are ids; resolve to names via the registry on SceneContext.AbilityRegistry (default Empty).
foreach (string id in def.Abilities) {
    int idx = registry.IndexOf(id);           // AbilityRegistry.cs:56
    string label = idx >= 0 ? registry.Get(idx).DisplayName : id;  // AbilityDefinition.cs:27; fallback to id
    listBox.AddChild(ChimeraListRow.Create(label)); // inert, no group (D-9)
}
```
No icon field exists on `AbilityDefinition` or `UnitDefinition` (grep-confirmed) — text-only. Do **not** copy `CommandCardSystem`'s ability read (`_world.AbilityId[...]`, CommandCardSystem.cs:641) — that needs a live spawned entity, excludes passives, and caps at 4; the card is def-driven. [Source: AbilityRegistry.cs:50-56; AbilityDefinition.cs:23-27; UnitDefinition.cs:126,229-264]

### In-panel 3D preview recipe (the #1 trap: world isolation)
Embed pattern from `MinimapBridge.cs:101-129`, **minus** its `World3D` share (line 166 — that renders the whole game world; the card needs its own). Use `OwnWorld3D = true`; add camera + lights + turntable as children of the SubViewport. Loader is crash-proof: `MeshLoader.LoadFromGlb` only loads when the path is non-empty AND `ResourceLoader.Exists`, else returns a `BoxMesh` placeholder — never throws (MeshLoader.cs:21-42). It uses `GD.Load<PackedScene>` (editor-imported GLBs), **not** `GLTFDocument` — correct for 3.3 (displays pre-imported faction GLBs); runtime GLTF ingest is a 3.5 concern, don't add it here. No mesh caching exists — QueueFree the prior `MeshInstance3D` on switch, and gate `UpdateMode` on visibility so a closed card renders nothing. Large GLBs (~18-30k verts) load synchronously on the main thread — acceptable for a per-open card, but don't background it. [Source: MeshLoader.cs:21-52; AssetPreviewScene.cs:97-162; MinimapBridge.cs:101-166; BuildingBridge.cs:91]

### Panel lifecycle + phase-wiring contract (clone the shape, not the palette)
`AbilityEditorPanel.cs` is the structural template: `Node` owning `CanvasLayer{Layer}` + `PanelContainer`; `_Ready→BuildUi`; phase `Initialize(...)` after `AddChild`; `Toggle`/`Close` flip `Visible`; `OnModeChanged` hides in Play. **But it predates the Theme and hardcodes a house palette + row-builders (`:33-42`, `:688-772`) — do NOT copy those**; build visuals from the kit. Phase registration is triple-guarded (`ScenePhaseRunner.AssertOrder` throws at boot + Tier-1 `PhaseOrderTest`): update `ScenePhaseOrder.Canonical` + the `MainScene` `ISetupPhase[]` + `PhaseOrderTest` in lockstep, and add the `SceneContext` field. CanvasLayer numbers in use: 0/5/8/10/12/13/14/15/20 → **11 or 16 free**. Editor keys in use: N/O/L/M/K + Escape → **`J` free**. [Source: AbilityEditorPanel.cs:23,93-120,129; AbilityEditorPhase.cs:13; ISetupPhase.cs:11; ScenePhaseOrder.cs:21; ScenePhaseRunner.cs:36; MainScene.cs:353-378,505-528; SceneContext.cs:58,104]

### UX-DR77 region blueprint (reconstructed — no pixel mock exists)
UX-DR77 is a one-line spec, "model, stats, abilities, economy, hero in one panel," and the Unit Card Editor was **"not re-mocked"** (EXPERIENCE.md:145) — so arrange from the WC3 consolidated-panel intent, not a mock. Suggested single-panel layout (top→bottom, or two columns):
- **Header:** DisplayName (title) · Id (subtitle) · archetype Tag · HERO Tag (if `is_hero`, D-6)
- **Model:** 3D preview viewport · `mesh_path` readout
- **Combat:** DamageType Tag · ArmorType Tag · attack · range · atk-interval readouts
- **Stats:** hp · speed · vision · supply readouts
- **Economy:** cost_ore · cost_crystal readouts
- **Abilities:** the resolved ability rows

`ChimeraTabs` (Overview/Stats/Abilities) is available if one flat panel gets crowded (D-8/optional). [Source: epics.md:348; EXPERIENCE.md:100,145; DESIGN.md:188]

### Verification posture
Split, mirroring prior UI stories (2.4b/2.8/3.1c): **Tier-1 (Godot-free xUnit)** covers the pure logic only — `FormatStat`, the ability-id→name resolver, and the `PhaseOrderTest` update (extract those to Godot-free statics so they're testable without the engine). The **visual/viewport ACs (AC1 render, AC2 preview + fallback, AC3 tooltips)** are proven by **`/godot-verify`** in-engine with screenshots (a Control panel + SubViewport can't be asserted headless). Confirm zero golden/checksum movement (presentation-only).

### Project Structure Notes
- New files: `godot/src/CreationSuite/UnitCardPanel.cs`, `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs`, Tier-1 tests under `godot/ProjectChimera.Sim.Tests/`. Edits: `SceneContext.cs`, `ScenePhaseOrder.cs`, `MainScene.cs`, `PhaseOrderTest.cs`.
- Conventions: `PascalCase.cs` matching class name; `#nullable enable`; Godot-inheriting classes are `partial`; editor panels live in `CreationSuite/` (not `UI/Components/`, which is the shared kit). [Source: project-context.md:131-135; game-architecture.md:1642,1679]
- No scene file is required (panels build their tree in code, per AbilityEditorPanel); if one is added it is `snake_case.tscn`.

### Project Context Rules (from project-context.md — apply to this story)
- **Sim/Presentation boundary is sacred.** This panel is presentation reading a content POCO; it must not read `EntityWorld`/stores or mutate anything. Data flows sim→presentation only. [project-context.md:75-81]
- **No `Fixed` in the read path** — def stats are authoring floats; format for display with normal C# formatting. [project-context.md:86-87]
- **Reuse existing systems** (`MeshLoader`, `AssetPreviewScene`, the 3.1 kit, `FactionDefinition`) rather than building parallel ones. [project-context.md:93-95]
- **Everything data-driven / progressive disclosure** — 3.3 is the "simple mode" display; the advanced/raw-JSON path is 3.4. [project-context.md:101]
- **Godot C# gotchas:** classes inheriting Godot types are `partial`; presentation may use `float` exports and `GD.Print`; the 4.6.3 engine target is unaffected by Control usage. [project-context.md:123-127]

### References
- Requirements & fence: `_bmad-output/planning-artifacts/epics.md:1239-1287` (Story 3.3/3.4/3.5), `:62-63,395-396` (FR-2/FR-3), `:348` (UX-DR77), `:297` (UX-DR34), `:322` (UX-DR53).
- Data model: `godot/src/Core/Definitions/UnitDefinition.cs` (field map above); `FactionDefinition.cs:26,99-115`; `UnitCategory.cs:14-22`; `Combat/DamageTable.cs:15-38`; sample `godot/resources/data/factions/alpha_faction.json:12-32`.
- UI kit: `godot/src/UI/Components/ChimeraComponents.{cs,Surfaces.cs,Controls.cs}` (`Panel` :23 / `Readout` :132 / `Chip` :95 / `Tag` :169 / `FieldLabel` :258 / init :81,147); `ChimeraListRow.cs:37`; `ChimeraTooltip.cs:43`; `ChimeraTabs.cs:37`; `Theme/ThemeTokens.cs`, `ThemeBuilder.cs:26`, `AccentController.cs`; `main.tres`; `ComponentGallery.cs:34-41`.
- Model preview: `godot/src/UI/MeshLoader.cs:21-52`; `AssetPreviewScene.cs:97-162`; embed pattern `MinimapBridge.cs:101-166`.
- Abilities + panel wiring: `AbilityRegistry.cs:50-56`; `AbilityDefinition.cs:23-27`; `CommandCardSystem.cs:641` (anti-pattern); `CreationSuite/AbilityEditorPanel.cs:23,93-129`; `Bootstrap/Phases/AbilityEditorPhase.cs:13`; `Bootstrap/ISetupPhase.cs:11`; `Bootstrap/ScenePhaseOrder.cs:21`; `Bootstrap/ScenePhaseRunner.cs:36`; `MainScene.cs:353-378,505-528`; `Bootstrap/Phases/SceneContext.cs:58,104`.
- UX/arch: `ux-Project_Chimera-2026-06-20/EXPERIENCE.md:57,100,145,156`, `DESIGN.md:152,188`; `game-architecture.md:1642,1679`; `project-context.md:75-135`.
- Baseline: git HEAD `fef8bad` (2026-07-06). Related memory: [[chimera-godot-theme-authoring-gotchas]], [[chimera-checksum-fold-timing-rule]], [[chimera-mesh-rendering-and-glb-format]], [[chimera-enum-indexed-array-touch-sites]].

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List

### Change Log

## Review Findings
