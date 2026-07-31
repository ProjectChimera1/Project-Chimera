---
title: 'DW-23: Consolidate copy-pasted kit-bootstrap + Heading/Body helpers into ChimeraComponents'
type: 'refactor'
created: '2026-07-31'
status: 'done'
baseline_revision: '5dafa8d07679df1a3c11031945eec5a9fa8c4ecb'
final_revision: '750e8773b990028bebc3369ab75ecbbaf4edc3e1'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/godot/src/UI/Components/ChimeraComponents.cs'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The kit-bootstrap `EnsureKitInitialized()` is copy-pasted verbatim into 19 UI consumers (18 byte-identical + `MatchAlertPhase`), and private `Heading`/`Body` label factories are copy-pasted into ~12 of them with real drift (e.g. `SettingsPanel.Body` forces `SizeFlagsVertical = ShrinkCenter`, `MainMenuOverlay.Body` does not; some `Body` copies apply a font override, others only a color). Every new kit-consuming overlay repeats the pattern and risks further per-consumer style drift (DW-23, flagged by the Blind Hunter layer on Story 3.11).

**Approach:** Single-source the **typography** into `ChimeraComponents` as static helpers — `EnsureInitialized(Node owner)`, `Heading(text, sizeToken)`, `Body(text, colorToken, sizeToken = null)` — then repoint every consumer to them and delete the private copies. This is a strictly **behavior-preserving** refactor: the static helpers own font/size/color only; each call site keeps its own contextual layout flags (`SizeFlagsVertical`, `AutowrapMode`, `SizeFlagsHorizontal`) so the produced `Label` is byte-for-byte equivalent to today's. Drift stops because color/font/size are now single-sourced.

## Boundaries & Constraints

**Always:**
- Preserve every consumer's current visual output exactly. The static `Heading`/`Body` apply ONLY font/size/color overrides; any layout flag a private helper baked in (ShrinkCenter, Autowrap Word/WordSmart, ExpandFill) MUST be re-applied at the call site so the resulting control is identical.
- Static `Body(text, colorToken, sizeToken)` semantics: always override `font_color` with `colorToken`; when `sizeToken` is non-null ALSO override `font` with `FontUi` and `font_size` with `SizeOf(sizeToken)`; when `sizeToken` is null apply neither (the theme's default font is inherited) — this reproduces both the legacy 2-arg color-only and 3-arg font+size forms.
- Static `Heading(text, sizeToken)`: override `font`=`FontDisplay`, `font_size`=`SizeOf(sizeToken)`, `font_color`=`TextHi`. Legacy 1-arg `Heading(text)` callers pass `ThemeTokens.Tlg` explicitly (their hardcoded size).
- Static `EnsureInitialized(Node owner)` returns the loaded theme (fresh `ResourceLoader.Load(..., CacheMode.Ignore) ?? ThemeBuilder.Build()`), parents the single `AccentController` to `owner`, and calls `ChimeraComponents.Initialize` ONLY when `!IsInitialized` — identical to the copied bodies. Each consumer keeps its own `_theme` field, assigned from the return value, because it uses `_theme` elsewhere (e.g. `panel.Theme = _theme`, `_theme.GetColor(...)`).
- Reuse the existing internal accessors `FontOf`/`SizeOf`/`Col` inside the new static helpers — do not re-read tokens by hand.
- Remove each now-dead private `EnsureKitInitialized`, `Heading`, `Body`, and the per-consumer `_accent` field (the accent is now owned via the static bootstrap; `_accent` is unused after conversion). Keep `_theme` fields.

**Block If:**
- A private `Body`/`Heading` copy is discovered whose typography (font/size/color tokens) cannot be reproduced by the static signatures without changing output AND cannot be preserved by a call-site layout tweak — HALT `blocked`, blocking condition `label helper does not map to static signature`.

**Never:**
- Never *unify* divergent layout behavior (do not force ShrinkCenter or an autowrap mode onto consumers that don't set it today; do not drop one that does). Layout is contextual and stays at the call site — changing it would regress intentional per-panel layout (e.g. `MatchBriefingOverlay`'s WordSmart + ExpandFill wrapping).
- Never introduce a shared base class — the consumers extend different Godot bases (`Node`, `CanvasLayer`), so a base is not viable; the static kit is the single source.
- Never change `SelectionSubgroupPanel` (it owns no copy — it free-rides on `MatchAlertPhase`'s bootstrap).

</intent-contract>

## Code Map

New single source:
- `godot/src/UI/Components/ChimeraComponents.Text.cs` -- NEW partial file: `EnsureInitialized`, `Heading`, `Body` static helpers.
- `godot/src/UI/Components/ChimeraComponents.cs` -- existing static kit; supplies `IsInitialized`, `Initialize`, `FontOf`/`SizeOf`/`Col`. No change unless a `using` is needed.

Bootstrap-only consumers (delete private `EnsureKitInitialized` + `_accent`; call static, assign `_theme`):
- `godot/src/CreationSuite/ItemCardPanel.cs`, `TechTreePanel.cs`, `DslGraphEditorPanel.cs`
- `godot/src/UI/InMatchMenuOverlay.cs`, `ScoreScreenOverlay.cs`
- `godot/src/Multiplayer/LobbyUi.cs`
- `godot/src/Core/Bootstrap/Phases/MatchAlertPhase.cs` -- divergent copy; parents accent to `_ctx.Scene` → call `ChimeraComponents.EnsureInitialized(_ctx.Scene)` (discard return; it has no `_theme` field).

Bootstrap + Heading/Body consumers (delete all three private copies + `_accent`; repoint call sites preserving layout flags):
- `godot/src/CreationSuite/UnitCardPanel.cs`, `BuildingCardPanel.cs`, `ResearchCardPanel.cs`, `FactionDefinerPanel.cs`, `PersistenceManifestPanel.cs`
- `godot/src/UI/HeroPickerOverlay.cs`, `OnboardingPanel.cs`, `SettingsPanel.cs`, `MatchBriefingOverlay.cs`, `MainMenuOverlay.cs`
- `godot/src/UI/TriggerDebugOverlay.cs`, `ObjectiveLogOverlay.cs` -- 1-arg `Heading(text)`; pass `ThemeTokens.Tlg`.

Also-duplicated label helpers (read exact bodies; map typography to static, keep any layout flag at the call site):
- `godot/src/UI/Components/ComponentGallery.cs`, `ComponentPreview.cs` -- private `Heading(string, StringName)` + `Body(string)`; the `Body(string)` form hardcodes its color/size — pass those explicit tokens to `ChimeraComponents.Body`.

## Tasks & Acceptance

**Execution:**
- `godot/src/UI/Components/ChimeraComponents.Text.cs` -- CREATE the three static helpers per the Boundaries semantics, using `FontOf`/`SizeOf`/`Col`, `ResourceLoader`, `ThemeBuilder`, `AccentController`; `namespace ProjectChimera.UI.Components`, `using Godot;`, `using ProjectChimera.UI.Theme;`.
- Bootstrap-only consumers (list above) -- replace the private `EnsureKitInitialized()` body with `_theme = ChimeraComponents.EnsureInitialized(this);` inlined at the single call site, delete the private method and the `_accent` field. For `MatchAlertPhase`, call `ChimeraComponents.EnsureInitialized(_ctx.Scene)` and discard the return; delete its private method (no `_accent`/`_theme` field there).
- Heading/Body consumers (list above) -- same bootstrap conversion, PLUS replace every `Heading(...)`/`Body(...)` call with `ChimeraComponents.Heading(...)`/`ChimeraComponents.Body(...)`, re-applying at each call site any layout flag the deleted private helper baked in (per the per-file drift: color-only-ShrinkCenter files add `SizeFlagsVertical = ShrinkCenter`; FactionDefiner/Onboarding also `AutowrapMode = Word`; MatchBriefing `AutowrapMode = WordSmart` + `SizeFlagsHorizontal = ExpandFill` and NO ShrinkCenter; MainMenu 3-arg no flag; Settings 3-arg + ShrinkCenter). Delete the private `Heading`/`Body` methods.
- `ComponentGallery.cs`, `ComponentPreview.cs` -- read the exact private `Heading`/`Body` bodies, repoint to the static helpers preserving typography and any layout flag, delete the private copies.

**Acceptance Criteria:**
- Given the refactor is complete, when `dotnet build godot/godot.csproj` runs, then it succeeds with no new warnings, and grep for `private .*EnsureKitInitialized` / `private .*Label Heading` / `private .*Label Body` across `godot/src/` returns zero matches (the only definitions live in `ChimeraComponents.Text.cs`).
- Given a consumer that previously baked `SizeFlagsVertical = ShrinkCenter` (or Word/WordSmart/ExpandFill) into its `Body`, when its call sites are inspected, then each produced label still sets exactly that flag — no flag added or removed versus the pre-refactor code.
- Given the full Tier-1 test suite is run, when it completes, then results match the pre-refactor baseline (no new failures).
- Given the game is launched to the Main Menu and the Settings panel is opened (both named in DW-23), when their heading/body labels are read from the live tree, then they render with the theme-derived font sizes their authoring tokens specify (in-engine gate).

## Review Triage Log

### 2026-07-31 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 0, low 3)
- defer: 2: (high 0, medium 1, low 1)
- reject: 7
- addressed_findings:
  - `[low]` `[patch]` `ChimeraComponents.Text.cs` class + `EnsureInitialized` doc comment overclaimed ("byte-for-byte", "drift can no longer creep in", magic counts 19/~12); rewrote to accurately scope typography-single-sourcing, caller-owned layout, and the demo/proof-harness exceptions.
  - `[low]` `[patch]` `EnsureInitialized` is a new public surface with no arg guard (NRE on null owner); added `ArgumentNullException.ThrowIfNull(owner)` + a `<param>` remark that owner must outlive the session.
  - `[low]` `[patch]` migration left redundant duplicate flag assignments (double `SizeFlagsVertical` in BuildingCardPanel/ResearchCardPanel/PersistenceManifestPanel `_counterLabel`/`switchLbl`; double `AutowrapMode.Word` on FactionDefinerPanel `_statusLabel`); removed the duplicates (byte-identical runtime state).

Deferred (recorded here for the orchestrator; the deferred-work ledger is not edited by this run per the invocation directive):
- `[medium]` Pre-existing latent: the single app-wide `AccentController` is parented to whichever kit consumer initializes first, and 18/19 callers pass transient `this`; if a closable panel is the first consumer, freeing it invalidates the shared accent until the next consumer re-bootstraps. Preserved unchanged from the pre-refactor per-panel bootstrap (not caused by this story). Location: `godot/src/UI/Components/ChimeraComponents.Text.cs:EnsureInitialized` + all `this`-passing callers.
- `[low]` No automated regression net for the consolidated typography helpers / UI label equivalence; the mandatory in-engine gate structurally under-covers a 20-file presentation change and `ProjectChimera.Sim.Tests` is Godot-free. Pre-existing UI-test-infra gap. Location: `godot/src/UI/Components/ChimeraComponents.Text.cs`.

Rejected (noise / by-design / pre-existing niche): Heading/Body "layout not owned → future drift" (deliberate typography/layout split); `Body` font-depends-on-sizeToken "footgun" (intended behavior-preserving semantics); `CacheMode.Ignore` per-consumer theme reload (pre-existing, reload-safety-intentional, negligible cost); `ThemeBuilder.Build()` divergent fallback instances (pre-existing, only on a broken build); `MatchAlertPhase` discards the return (correct — no `_theme` field); `ThemePreview.cs` retains a private `Heading` (out of scope — standalone proof harness that never calls `Initialize`); intent Reading B "unify layout" (explicitly resolved against in the intent-contract `Never`).

## Design Notes

The consumers extend different Godot base types (`Node` vs `CanvasLayer`), so a shared C# base class cannot cover them — the static kit is the correct single source, and it already holds the `_theme`/`_accent` context and the `FontOf`/`SizeOf`/`Col` accessors. Golden shape of the new helpers:

```csharp
public static Godot.Theme EnsureInitialized(Node owner) {
    var theme = ResourceLoader.Load<Godot.Theme>(ThemeBuilder.ThemePath,
        cacheMode: ResourceLoader.CacheMode.Ignore) ?? ThemeBuilder.Build();
    if (!IsInitialized) {
        var accent = new AccentController { Name = "AccentController" };
        owner.AddChild(accent); accent.Initialize(theme);
        Initialize(theme, accent);
    }
    return theme;
}
public static Label Body(string text, StringName colorToken, StringName? sizeToken = null) {
    var l = new Label { Text = text };
    if (sizeToken != null) {
        l.AddThemeFontOverride("font", FontOf(ThemeTokens.FontUi));
        l.AddThemeFontSizeOverride("font_size", SizeOf(sizeToken));
    }
    l.AddThemeColorOverride("font_color", Col(colorToken));
    return l;
}
```

`Heading` mirrors `Body` but always applies FontDisplay + size + TextHi. The behavior-preservation contract is the whole point: layout flags are contextual and MUST stay with the caller; only typography is single-sourced.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds, no new warnings.
- `dotnet test` (Tier-1 suite) -- expected: no new failures vs baseline.
- grep `private\s+(static\s+)?(void\s+EnsureKitInitialized|Label\s+Heading|Label\s+Body)` under `godot/src/` -- expected: zero matches outside `ChimeraComponents.Text.cs`.

**In-engine gate (required — touches `godot/src/UI/**`, `CreationSuite/**`, `Core/Bootstrap/**`):**
- Build, launch via godot-mcp, reach Main Menu → open Settings (emit the Settings button `pressed` signal). Read a Heading and a Body label from the live tree; assert their `font_size` equals `SizeOf(token)` for the authoring token. Append the `### In-Engine Gate - <date>` block with the captured digest. PASS only if the app boots (proving all 19 files compile against the new API) and the labels render with theme-correct typography.

### In-Engine Gate - 2026-07-31
- surface: Main Menu (kit consumer #1) → Settings panel (kit consumer #2), driven the way a player would.
- launched: `dotnet build godot/godot.csproj` (Build succeeded, 0 errors) → `godot_editor_edit run` via godot-mcp (bridge reachable), zero editor/runtime errors at boot; opened Settings by emitting the visible `SETTINGS` button's `pressed` signal; labels read from the live tree via `godot_exec`.
- digest: authoring source `godot/assets/ui/main.tres` (type "Chimera") — t_lg=18, t_xl=23, t_4xl=52, t_sm=13; text_hi=(0.9333,0.9490,0.9647), text_mid=(0.6824,0.7176,0.7608). Observed live: "PROJECT CHIMERA" `Heading(T4xl)` font_size=52, color=text_hi, font override=true; "Build the game. Then play it." `Body(TextMid,Tlg)` font_size=18, color=text_mid, font override=true; "Settings" `Heading(Txl)` font_size=23, color=text_hi, override=true; five Settings field labels `Body(TextMid,Tsm)` (Show FPS / Show minimap / Edge scroll / Zoom speed / Pan speed) font_size=13, color=text_mid, override=true. Live-tree search for `*AccentController*` returned exactly ONE node: `/root/MainScene/@CanvasLayer@4/AccentController`.
- asserted: every Heading/Body label's `font_size`, `font_color`, and font-override presence equal the theme token the call site passes (expected vs observed matched on all 8+ assertions, real numbers above). The single-AccentController invariant held across two consumers — the second consumer (Settings) ran `EnsureInitialized` as a no-op and created no second accent, exactly as the deleted per-panel `if (!IsInitialized)` guards did.
- result: PASS

## Auto Run Result

Status: done
Blocking condition: none

**Change:** Repair session for a deterministic-verification FALSE-NEGATIVE — no code change was required and none was made. The prior dev pass fully implemented DW-23 and committed it at `750e877` (build 0-errors, Tier-1 3714/0/1, In-Engine Gate PASS with real runtime digests). The orchestrator's `tools/verify-in-engine-gate.ps1` gate nonetheless failed rc=1 with *"story spec not found at the convention path for story_key 'dw-overlay-kit-bootstrap-consolidation'"*. Root cause: `state.json.spec_file` is empty at the moment the verify command runs (the path only reaches `state.json` after the dev result is merged back — the script's own header documents this as the normal case), so the gate fell back to the `spec-<story_key>.md` convention path `spec-dw-overlay-kit-bootstrap-consolidation.md`, while this spec is named `spec-dw-23-overlay-kit-bootstrap-consolidation.md` (planning prefixed the DW number). By the time this repair session runs, `state.json.spec_file` is populated with the real path, so the gate resolves via the declared path, finds the intact `### In-Engine Gate - 2026-07-31` PASS block, and now returns **exit 0** (re-run and confirmed this session). The repair therefore was: (1) confirm the gate passes on the real thing, (2) confirm code integrity is unchanged, (3) restore the completed-spec handback state that the orchestrator stripped when it re-opened the spec for this repair pass.

**Files changed:**
- `_bmad-output/implementation-artifacts/spec-dw-23-overlay-kit-bootstrap-consolidation.md` — restored frontmatter `status: done` and re-appended this `## Auto Run Result` (the orchestrator strips the prior section on re-open). Intent-contract untouched; no code files touched.

**Verification (independently re-run this session):**
- `tools/verify-in-engine-gate.ps1` → `[in-engine-gate] PASS - in-engine artifact present in spec-dw-23-overlay-kit-bootstrap-consolidation.md.` (**exit 0**). This is the sole `[verify]` command in `.bmad-loop/policy.toml`; it is the gate that had failed, and it now passes against the current tree.
- grep AC `private\s+(static\s+)?(void\s+EnsureKitInitialized|Label\s+Heading|Label\s+Body)` under `godot/src/` → single match `godot/src/UI/Theme/ThemePreview.cs:222`, the documented out-of-scope proof harness (never calls `Initialize`); the sole live definitions remain in `ChimeraComponents.Text.cs`. AC satisfied.
- Code is unchanged since the reviewed/committed `750e877`; `dotnet build` / `dotnet test` were not re-run (no code delta to re-validate, and neither is a `[verify]` gate command — the dev pass recorded 0-errors / 3714-0-1).

**Residual risks / notes:** (1) Latent naming inconsistency — the spec filename (`spec-dw-23-…`) does not match the gate's `spec-<story_key>.md` convention fallback; harmless while `state.json.spec_file` stays populated (the declared-path branch resolves first), which it does. Worth normalizing at the tooling layer (planning-slug vs. orchestrator story_key) so a future empty-`spec_file` verify can't re-trip the fallback. (2) Per the invocation directive, the frozen intent-contract was not modified and the deferred-work ledger was not edited by this session (its working-tree `DW-23 → done` marking is the sweep orchestrator's, left untouched).

