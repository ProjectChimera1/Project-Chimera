---
title: 'Story 8.4 — AI entity drafts as editable data: unit / ability / hero / faction generation on the provider stack'
type: 'feature'
created: '2026-07-21'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '05c41e5d54681f2ca44885e22a22193ea6ca3c34'
final_revision: '5eff64491eff352284ba2b0f937655a6810f3449'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-8-context.md'
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Epic 8 gives creators AI generation for triggers and maps (8.3) but not for the core content entities — units, abilities, heroes, factions. Creators want a fast, prompt-driven *starting point* for those entities, but only if it is fully editable data that passes the exact same validation gate as hand-authored content — never a locked black box, never a determinism hazard.

**Approach:** Add a provider-backed **entity draft framework** to the existing Godot-free `LLMService`, mirroring `GenerateTriggerAsync`/`GenerateScenarioAsync`: four new `Generate{Unit,Ability,Hero,Faction}DraftAsync` methods, each with an internal-static prompt builder and a public-static validate router that deserializes the model output through the **existing** definition serializer and gates it with the **existing** validator for that kind (`UnitDefinitionValidator`, `AbilityValidator`/`AbilityLoader`, `FactionValidator`). The validated draft is handed to the editor panel's existing editable-JSON seam as a normal, reopenable definition. No second float→Fixed path is introduced — quantization stays exactly where hand-authored data has it (abilities: `FixedJsonConverter` at parse; units/heroes/factions: `EntityWorld.ApplyUnitDefinition` at the sim boundary), so a draft hashes identically to an equivalent hand-authored def.

## Boundaries & Constraints

**Always:**
- `godot/src/AI/LLMService.cs` stays Godot-free (`grep "using Godot"` returns nothing) and pumps callbacks through the existing `ConcurrentQueue<Action>` + `DrainEvents()` marshalling; each of the four kinds owns its own `CancellationTokenSource`.
- Every draft routes through the SAME deserialize + validate path hand-authored data uses for that kind. Reuse the existing validators verbatim; do not fork or weaken them. Invalid generated fields are rejected with the validator's **located** error (path + offending value), surfaced through the `Action<TDef?, string?>` callback — never silently accepted.
- The selected provider is authoritative with NO fallback (inherit the 8.3 pipeline): `LlmProviderFactory.TryCreate` → `provider.GenerateAsync(NormalizedRequest)` → on unavailable/failure voice the four-state message via `AiAvailabilityMessages.Describe(...)` / `AiAvailabilityMap.FromFailure(...)`; the key is read only via `ISecretStore`. When no provider/key is configured, NO network request is made.
- The quantize-before-hash contract is satisfied by reuse, not a new path: ability drafts deserialize through `ContentJson.Options` (numbers land as `Fixed`, out-of-range rejected by `FixedJsonConverter`); unit/hero/faction drafts deserialize through `FactionDefinition.JsonOptions` (plain float) and are range-gated to the Fixed-safe `[0, 32768)` by `UnitDefinitionValidator`, with quantization occurring at the same `ApplyUnitDefinition` boundary as hand-authored units.
- Drafts express behavior via archetype + ability/behavior composition (referencing existing ability/behavior ids), never RTS-only bespoke subclasses. The generated draft is always editable and reopenable in the manual editor; nothing is locked.
- Every AI affordance degrades gracefully: with AI unavailable, the affordance disables and shows the four-state message while the manual editor flow is completely unaffected.

**Block If:**
- A required existing validator/serializer for a kind cannot gate a generated draft without being modified in a way that would change how *hand-authored* data of that kind is validated (would mean the "same gate" invariant is unachievable as specified).

**Never:**
- No AI/LLM code in the sim tick; generation is authoring-layer only (`LLMService` is Tier-1). No new provider/adapter — reuse the 8.1–8.3 stack.
- No second float→Fixed quantization path and no new canonical hash over a bare `UnitDefinition`/`HeroDefinition`/`FactionDefinition`.
- Do not re-serialize faction files via reflection — unit/faction persistence stays on the DOM-preserving `FactionWriter` seam the manual editor already uses (avoids leaking `[JsonIgnore]` `Parsed*` getters). This story hands drafts to the editor's existing save path; it does not add a new writer.
- Do not gate generation on roster-completeness (`FactionValidator.ValidateComplete`): an incomplete-but-well-formed faction draft must still load for further editing. Completeness stays enforced at the existing selectable-load/save boundary, unchanged.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Unit draft happy path | Configured provider + stub returns valid unit JSON; `GenerateUnitDraftAsync` | Callback fires with a non-null `UnitDefinition` and null error; provider `CallCount == 1` | No error expected |
| Ability draft happy path | Stub returns valid ability JSON; `GenerateAbilityDraftAsync` | Callback fires with a non-null `AbilityDefinition` (numbers materialized as `Fixed`), null error | No error expected |
| Hero draft happy path | Stub returns unit JSON with `is_hero:true` + `hero{}` block; `GenerateHeroDraftAsync` | Callback fires with a non-null `UnitDefinition` where `IsHero` and `Hero != null` | No error expected |
| Faction draft happy path | Stub returns valid faction JSON; `GenerateFactionDraftAsync` | Callback fires with a non-null `FactionDefinition` whose units passed per-unit validation | No error expected |
| Float out of Fixed range | Generated unit with `attack_damage: 40000` | `ValidateUnitDraft` returns `(null, located error)` naming the path + value; callback carries the error | Located reject |
| Out-of-range ability number | Ability JSON with `cooldown: 99999.0` | `ValidateAbilityDraft` returns `(null, located error)` from `FixedJsonConverter`/`AbilityValidator` | Located reject |
| Hero block missing | Unit JSON with `is_hero:false` or no `hero{}` via `GenerateHeroDraftAsync` | `ValidateHeroDraft` returns `(null, located error)` explaining a hero draft requires `is_hero:true` + a `hero` block | Located reject |
| Faction with an invalid unit | Faction JSON whose one unit has an unknown archetype/ability ref | `ValidateFactionDraft` runs per-unit `UnitDefinitionValidator` and returns `(null, located error)` naming the unit + field | Located reject (closes the faction-load deep-validation gap) |
| No provider / no key | Provider unset or key absent; any `Generate*DraftAsync` | Callback carries the distinct four-state message; NO network request (`CallCount == 0`) | Four-state message |
| Provider failure | Selected provider's endpoint 401/unreachable | Callback carries the four-state message via `AiAvailabilityMap.FromFailure`; no other provider tried | Four-state message |
| Markdown-fenced response | Valid JSON wrapped in ```` ```json ```` fences | `StripMarkdown` unwraps it; draft validates and reaches the callback | No error expected |
| Prompt staleness | A closed-vocabulary member (ability effect kind / faction `ai_preset`) absent from its prompt builder | The prompt staleness-guard test fails | Test guard |

</intent-contract>

## Code Map

- `godot/src/AI/LLMService.cs` -- EDIT (primary). Add four kinds. (1) Four context DTOs (Godot-free): `UnitDraftContext { AbilityRegistry?, BehaviorRegistry?, ItemRegistry?, IReadOnlyList<UnitDefinition>? Siblings }` (reused for hero), `AbilityDraftContext` (optional existing-id hints; validation self-contained), `FactionDraftContext { AbilityRegistry?, BehaviorRegistry?, ItemRegistry?, IReadOnlyList<string> AiPresets, IReadOnlyList<string> SignatureIds }`. (2) A shared internal `RunDraftAsync<T>(systemPrompt, userMsg, Func<string,(T?,string?)> validate, ref CancellationTokenSource, Action<T?,string?>)` factoring the `Task.Run → snapshot settings → TryCreate (four-state on false, no request) → GenerateAsync → !Ok four-state via FromFailure → StripMarkdown → validate → enqueue` pipeline exactly as `GenerateTriggerAsync` (:152) does; the four public `Generate{Unit,Ability,Hero,Faction}DraftAsync(prompt, ctx, onComplete)` are thin wrappers each with its own CTS. (3) Four internal-static prompt builders `Build{Unit,Ability,Hero,Faction}DraftPrompt(ctx)` — each states the kind's JSON schema (snake_case), the Fixed-safe numeric range, archetype+ability composition guidance, available ids from `ctx`, and "Return ONLY valid JSON. No markdown fences." — staleness-guardable where they enumerate a closed set. (4) Four public-static validate routers `Validate{Unit,Ability,Hero,Faction}Draft(string json, ctx)` returning `(TDef?, string? error)`.
- `godot/src/Core/Definitions/UnitDefinition.cs` / `FactionDefinition.cs` -- READ/REUSE. `FactionDefinition.JsonOptions` (:184) is the lenient (no-converter) options for unit/hero/faction deserialize. `UnitDefinition` float fields are plain `float` (quantized later at the sim boundary).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- READ/REUSE. `Validate(def, AbilityRegistry?, BehaviorRegistry?, ItemRegistry?, siblings, kind="unit")` (:139) → `UnitValidationResult` (list of located `(FieldPath, Message)`; float range `[0,32768)`; runs `ValidateHero` for `IsHero`). This gate covers unit AND nested hero.
- `godot/src/Core/Definitions/AbilityLoader.cs` / `AbilityValidator.cs` -- READ/REUSE. `AbilityLoader.Load(json, sourceLabel)` (:22) deserializes via `ContentJson.Options` (`FixedJsonConverter` → `Fixed`, out-of-range rejected) and runs `AbilityValidator` → `AbilityValidationResult` (`.Ok`, first-fail located `Error`, `.Value`). This is the ability draft router body.
- `godot/src/Core/Definitions/FactionValidator.cs` -- READ/REUSE. `Validate(FactionDefinition)` (:74) structural gate; the faction router runs it AND loops `UnitDefinitionValidator` over `def.Units` to close the documented deep-validation gap. Do NOT call `ValidateComplete` here (roster-completeness stays at the selectable boundary).
- `godot/src/Core/Definitions/FixedPoint.cs` / `FixedJsonConverter.cs` -- REFERENCE. `Fixed` 16.16; `FixedRangeLimit = 32768`. The single sanctioned float→Fixed on external data (ability path only).
- `godot/src/AI/Providers/*` -- REFERENCE/REUSE unchanged. `LlmProviderFactory.TryCreate`, `NormalizedRequest`/`NormalizedResult`, `AiAvailabilityMessages.Describe`, `AiAvailabilityMap.FromFailure`, `AiAvailabilityEvaluator.EvaluateConfig`.
- `godot/src/CreationSuite/UnitCardPanel.cs` (+ `.Edit.cs`) -- EDIT. Add an AI draft affordance (prompt input, Generate button, a Hero toggle selecting unit-vs-hero, `ChimeraSpinner`+"Transmuting…", four-state `_aiAvailLabel` from `EvaluateConfig`, `DrainEvents()` in `_Process`). On callback, hand the validated `UnitDefinition` into the existing raw-JSON/`SaveFromRawPane`/`Bind` editable seam (`.Edit.cs:1147`) so it appears editable and reopenable. Mirror `MapGeneratorPanel` structure.
- `godot/src/CreationSuite/AbilityEditorPanel.cs` -- EDIT. Same affordance; on callback feed the validated ability into `ApplyJson`/`ReflectModelIntoForm` (:487/:498).
- `godot/src/CreationSuite/FactionDefinerPanel.cs` -- EDIT. Same affordance; on callback populate `_draft` / raw-JSON pane (`SetJsonPaneText`, :213).
- `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs`, `AbilityEditorPhase.cs`, `FactionDefinerPhase.cs` -- EDIT. Widen each `Initialize(...)` to also pass `_ctx.LlmService`, `_ctx.AiEvaluator`, `_ctx.SecretStore` (all present on `SceneContext` since `SettingsPhase`/`TriggerEditorPhase` run earlier) into the panel, matching how `TriggerEditorPhase` (:48) wires the trigger panel. Nullable — a null evaluator hides the AI row (older-wiring fallback).
- `godot/ProjectChimera.Sim.Tests/AI/` -- NEW test files (below). Reuse `StubHttpMessageHandler` (`.Ok/.Status/.Unreachable`, `CallCount`), `FakeSecretStore`, and the `Pump(svc, () => done)` `DrainEvents()` loop from `LlmServiceRepointTests`.

## Tasks & Acceptance

**Execution:**
- `godot/src/AI/LLMService.cs` -- Add the four context DTOs, the shared `RunDraftAsync<T>` pipeline, the four `Generate*DraftAsync` methods (each own CTS, drained via existing `DrainEvents()`), the four internal-static `Build*DraftPrompt` builders, and the four public-static `Validate*Draft` routers -- provider-backed editable-draft framework for all four entity kinds, reusing the 8.3 no-fallback/four-state/StripMarkdown pipeline and the existing per-kind validators; stays Godot-free.
- `godot/src/CreationSuite/UnitCardPanel.cs` (+ `.Edit.cs`) -- Add the AI draft affordance (unit + hero via a toggle) wired to `GenerateUnitDraftAsync`/`GenerateHeroDraftAsync`, feeding the validated draft into the existing editable-JSON seam; four-state label + `ChimeraSpinner` "Transmuting…"; `DrainEvents()` in `_Process` -- unit & hero drafts appear as editable, reopenable data with the mandated spinner/graceful-degradation UX.
- `godot/src/CreationSuite/AbilityEditorPanel.cs` -- Add the AI draft affordance wired to `GenerateAbilityDraftAsync`, feeding the validated ability into `ApplyJson`/`ReflectModelIntoForm`; four-state label + spinner + drain -- ability drafts appear as editable data.
- `godot/src/CreationSuite/FactionDefinerPanel.cs` -- Add the AI draft affordance wired to `GenerateFactionDraftAsync`, populating `_draft`/raw-JSON pane; four-state label + spinner + drain -- faction drafts appear as editable data.
- `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs`, `AbilityEditorPhase.cs`, `FactionDefinerPhase.cs` -- Widen `Initialize` to pass `LlmService`/`AiEvaluator`/`SecretStore` from `SceneContext` -- three wiring sites; no plaintext key; AI deps available to each editor.
- `godot/ProjectChimera.Sim.Tests/AI/EntityDraftGenerationTests.cs` -- Per kind: stub happy path (callback gets a validated non-null def, `CallCount==1`); NoProvider/NoKey short-circuit (four-state message, `CallCount==0`); provider failure (four-state message, no second attempt); fenced-JSON stripped -- proves the pipeline is consumed with no fallback and no network when unavailable.
- `godot/ProjectChimera.Sim.Tests/AI/EntityDraftValidationTests.cs` -- Per router: valid JSON → def; out-of-Fixed-range float → located error (asserts path + value); unknown enum/archetype/ability-ref → located error; hero JSON with `is_hero:false`/no `hero{}` → located error; faction with one invalid unit → per-unit located error -- covers the reject rows and the closed faction deep-validation gap.
- `godot/ProjectChimera.Sim.Tests/AI/EntityDraftPromptTests.cs` -- Staleness guards: `BuildAbilityDraftPrompt` names every member of the closed effect/targeting/activation vocabulary (exact-line-token match, per the 8.3 guard style); `BuildFactionDraftPrompt` names every `ai_preset` closed-set member; `BuildUnitDraftPrompt`/`BuildHeroDraftPrompt` state the Fixed-safe range and archetype+ability composition guidance -- prevents prompt drift from the registries/enums.
- `godot/ProjectChimera.Sim.Tests/AI/EntityDraftQuantizeTests.cs` -- Quantize-before-hash observable: an ability draft numeric (e.g. `cooldown: 1.333333`) round-trips equal to `Fixed.FromFloat(1.333333)` after `ValidateAbilityDraft`; a unit draft with an out-of-Fixed-range float is rejected while an in-range float is accepted and, applied via the existing `ApplyUnitDefinition` path, yields the same SoA `Fixed` as an equivalent hand-authored def -- proves quantization reuses the existing gate/boundary, not a second path.

**Acceptance Criteria:**
- Given a configured provider and a stub endpoint, when each `Generate{Unit,Ability,Hero,Faction}DraftAsync` runs with a valid response, then its callback receives a non-null definition of the matching type that passed the SAME validator hand-authored data of that kind uses, and provider `CallCount == 1`.
- Given a generated draft whose numeric field exceeds the Fixed-safe range, or whose enum/archetype/ability reference is unknown, when its validate router runs, then it returns `(null, located error)` naming the path and offending value and the callback carries that error — no invalid field is silently accepted.
- Given `GenerateHeroDraftAsync`, when the response lacks `is_hero:true` or a `hero{}` block, then `ValidateHeroDraft` rejects it with a located error; and given a valid hero response, the returned `UnitDefinition` has `IsHero` set and a non-null `Hero`.
- Given a faction draft containing a unit that would fail `UnitDefinitionValidator`, when `ValidateFactionDraft` runs, then it rejects with a per-unit located error (the faction gate deep-validates units, unlike bare faction load).
- Given no provider or no key configured, when any `Generate*DraftAsync` runs, then the callback carries the distinct four-state message and no network request is made; given a selected provider whose endpoint fails, the callback carries the four-state failure message and no other provider is invoked.
- Given a closed-vocabulary member (ability effect kind, faction `ai_preset`) absent from its prompt builder, when the suite runs, then the corresponding prompt staleness-guard test fails.
- Given `godot/src/AI/LLMService.cs`, when inspected, then `grep "using Godot"` returns nothing and no second float→Fixed path or new bare-definition canonical hash is introduced.

## Design Notes

**Quantize-before-hash = reuse, not a new path (the load-bearing decision).** The epic's "float quantized to Fixed by the SAME validation gate before persistence or any canonical hash" is satisfied by routing generated JSON through the *existing* per-kind path, because the two definition stacks already differ deliberately: abilities deserialize via `ContentJson.Options` where `FixedJsonConverter` quantizes at parse and rejects `|v| >= 32768`/NaN/Inf; units/heroes/factions deserialize via the lenient `FactionDefinition.JsonOptions` (plain float) and only quantize at `EntityWorld.ApplyUnitDefinition` (the single def→SoA float→Fixed boundary that feeds `SimChecksum`). A draft therefore hashes identically to an equivalent hand-authored def *by construction*. Introducing a bespoke quantize step or a bare-definition hash would fork behavior and is explicitly forbidden. The validators enforce the Fixed-safe `[0,32768)` range so an un-quantizable value is a located reject, not a downstream determinism surprise.

**Hero = a unit with `is_hero:true` + a `hero{}` block.** There is no standalone hero file/entity; hero validity is enforced inside `UnitDefinitionValidator.ValidateHero`. `GenerateHeroDraftAsync` thus returns a `UnitDefinition`; its router additionally requires `IsHero` + non-null `Hero` before the shared unit validation, and the UnitCard panel's Hero toggle selects it (the promote-to-hero form already renders the hero rows).

**Faction draft closes a known gap.** Bare faction load (`FactionDefinition.LoadFromFile`) runs only structural `FactionValidator.Validate` and does NOT deep-validate each unit. To honor "passes the same validation gate as hand-authored data" meaningfully, `ValidateFactionDraft` runs `FactionValidator.Validate` AND loops `UnitDefinitionValidator` over `def.Units`. Roster-completeness (`ValidateComplete`) is deliberately NOT run at generation — an incomplete draft must remain editable/reopenable; completeness is enforced unchanged at the existing selectable-load/save gate.

**Golden pipeline shape** (matches `GenerateTriggerAsync`):
```
Generate{Kind}DraftAsync(prompt, ctx, onComplete):
  cts = fresh;  settings = _getSettings()            // snapshot on caller thread
  Task.Run:
    if !LlmProviderFactory.TryCreate(settings, _secretStore, _http, out p, out failure):
        enqueue onComplete(null, AiAvailabilityMessages.Describe(failure)); return   // no request
    r = await p.GenerateAsync(new NormalizedRequest(Build{Kind}DraftPrompt(ctx), prompt, MAX_TOKENS), token)
    if !r.Ok: enqueue onComplete(null, AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(r.Failure))); return
    (def, err) = Validate{Kind}Draft(StripMarkdown(r.Text), ctx)
    enqueue onComplete(def, def == null ? err : null)
```

**UI is Godot-coupled (manual-verified), logic is Tier-1.** The panels/phase-wiring/spinner/four-state-label are not reachable by the headless harness — same boundary 8.1–8.3 accepted. The load-bearing surface (prompt builders, validate routers, the async generate pipeline, no-fallback/no-network behavior, quantization reuse) is fully Tier-1 covered via `StubHttpMessageHandler`/`FakeSecretStore` and directly-constructed registries (`new AbilityRegistry(new[]{...})`).

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including the new `AI/EntityDraftGenerationTests`, `AI/EntityDraftValidationTests`, `AI/EntityDraftPromptTests`, `AI/EntityDraftQuantizeTests`; no prior test regresses.
- `dotnet build godot/godot.sln` -- expected: full solution compiles with the new service surface + panel/phase wiring.
- `grep -n "using Godot" godot/src/AI/LLMService.cs` -- expected: no matches (stays Godot-free / Tier-1 + analyzer covered).

**Manual checks (in-engine, Godot-coupled UI not reachable by the headless harness):**
- Open the Unit Card editor (J) with a configured provider + key: generate a unit draft from a prompt; confirm the "Transmuting…" spinner shows, the draft populates the editable form and can be further edited and saved, and toggling Hero generates a draft with the hero leveling rows populated.
- Open the Ability editor (K) and Faction definer (X): generate a draft each; confirm it lands as editable data and the review/edit-before-save flow still works.
- Clear the key: confirm each editor's AI row disables and shows the four-state message while manual authoring remains fully usable (graceful degradation).

## Spec Change Log

_No bad_spec loopback occurred; empty._

## Review Triage Log

### 2026-07-21 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 3, low 1)
- defer: 5
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` Faction draft's per-unit ability-reference check was inert in production: `FactionDefinerPanel.OnAiGeneratePressed` built `FactionDraftContext` with null registries, so the router's per-unit `UnitDefinitionValidator` loop skipped ability/behavior/item ref checks (null-registry semantics) — the "deep-validation gap" the Design Notes claim to close ran only archetype/range/id at draft time, deferring ref checks to Finish. Wired a loaded `AbilityRegistry.LoadFromDirectory(ABILITIES_DIR_RES)` (the same load the Finish gate uses) into the ctx, and added `ValidateFactionDraft_UnitAbilityRef_WithRegistry_LocatedReject` proving a drafted unit citing a missing ability id is rejected at draft time. (Blind Hunter / Verification Gap / Intent Alignment / Edge Case Hunter — deduped)
  - `[medium]` `[patch]` `MAX_TOKENS = 2048` was shared across all four kinds; a faction draft echoes the full unit schema for every unit in a playable roster (plus buildings) and truncates mid-JSON under 2048, surfacing only a generic "Invalid JSON". Threaded a per-kind `maxTokens` into `RunDraftAsync` and gave faction `FACTION_DRAFT_MAX_TOKENS = 8192`. (Edge Case Hunter / Blind Hunter — deduped)
  - `[medium]` `[patch]` The unit-draft landing's id-uniquify was the SOLE guard keeping the roster duplicate-free (the draft is validated with `Siblings == null`, so `UnitDefinitionValidator` skips the dup-id rule), yet it lived in an untested Godot panel method. Extracted the dedup convention into Godot-free, Tier-1-tested `UnitDefinitionValidator.MakeUniqueId(existingIds, baseId)`; the panel's `UniqueId` now delegates to it (unifying the manual New/Duplicate and AI paths onto one tested convention). Added 3 `MakeUniqueId` tests (no-collision, suffix-until-free, sanitize/fallback). (Verification Gap / Edge Case Hunter — deduped)
  - `[low]` `[patch]` `ShowAiStatus` had drifted between the three panel copies: `AbilityEditorPanel` color-distinguishes failure (red) from success (green), but the `UnitCardPanel` and `FactionDefinerPanel` copies dropped the `error` parameter, rendering a failed generation in the same neutral color as success. Restored the `(message, error)` signature + theme `Danger`/`Ok` coloring in both, and passed `error: true` on the failure branches. (Blind Hunter)

## Auto Run Result

Status: done

**Summary:** Added a provider-backed **entity draft framework** to the Godot-free `LLMService`, mirroring the Story 8.3 trigger/map pipeline: four `Generate{Unit,Ability,Hero,Faction}DraftAsync` methods (each with its own `CancellationTokenSource`) over a shared `RunDraftAsync<T>` (snapshot settings → `LlmProviderFactory.TryCreate` four-state + no-request short-circuit → `provider.GenerateAsync` → `!Ok` four-state via `AiAvailabilityMap.FromFailure` → `StripMarkdown` → validate → marshal via `DrainEvents`), four internal-static `Build*DraftPrompt` builders (staleness-guarded on closed vocabularies), and four public-static `Validate*Draft` routers that each route through the EXISTING per-kind gate (`UnitDefinitionValidator`, `AbilityLoader`/`AbilityValidator`, `FactionValidator` + a per-unit `UnitDefinitionValidator` loop). The quantize-before-hash contract is honored by reuse, not a new path (abilities quantize to `Fixed` at parse via `ContentJson.Options`; units/heroes/factions stay float and range-gate to `[0,32768)`, quantizing only at `EntityWorld.ApplyUnitDefinition`). The three entity editors (Unit Card incl. hero-toggle, Ability, Faction Definer) gained an AI draft affordance (prompt input, `ChimeraSpinner` "Transmuting…", four-state availability line, `DrainEvents()` pump) that lands the validated draft in each editor's existing editable seam; the three host phases were widened to pass `LlmService`/`AiEvaluator`/`SecretStore`.

**Files changed:**
- `godot/src/AI/LLMService.cs` — the draft framework (4 context DTOs, `RunDraftAsync<T>` with a per-kind `maxTokens`, 4 generate methods, 4 prompt builders, 4 validate routers); faction budget `FACTION_DRAFT_MAX_TOKENS = 8192` (review P3).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — new Godot-free `MakeUniqueId(existingIds, baseId)` dedup helper (review P4).
- `godot/src/CreationSuite/UnitCardPanel.cs` (+ `.Edit.cs`) — unit/hero AI affordance landing into the `_faction.Units` + `PushHistory` seam; `ShowAiStatus` failure coloring (P1); `UniqueId` delegates to `MakeUniqueId` (P4).
- `godot/src/CreationSuite/AbilityEditorPanel.cs` — ability AI affordance landing into `LoadFromRegistry`.
- `godot/src/CreationSuite/FactionDefinerPanel.cs` — faction AI affordance landing into `_draft`/Advanced pane; ctx now loads a real `AbilityRegistry` (P2); `ShowAiStatus` failure coloring (P1).
- `godot/src/Core/Bootstrap/Phases/{UnitCardPhase,AbilityEditorPhase,FactionDefinerPhase}.cs` — widened `Initialize` to pass the AI deps from `SceneContext`.
- `godot/ProjectChimera.Sim.Tests/AI/EntityDraft{Generation,Validation,Prompt,Quantize}Tests.cs` + `EntityDraftTestData.cs` — the Tier-1 suite (happy path/no-fallback/four-state/no-network, per-router validation incl. the P2 registry ref-reject and P4 `MakeUniqueId`, prompt staleness guards, quantize-by-reuse).

**Review findings:** 4 patches applied (medium 3, low 1 — see Review Triage Log). 5 deferred (faction Generate overwrites in-progress wizard draft with no undo/confirm; the per-kind cancellation apparatus is dead/unwired and would leave the spinner stuck if ever wired, plus a late callback after Close mutates a hidden panel; the AI-card UI wiring seams — spinner, availability-line hide-on-null-deps, faction-pane population, ability `LoadFromRegistry` landing, unit-ctx `ItemRegistry` omission — have no automated coverage; the three near-identical AI-card blocks should be a shared control; the intent names "lore" but no definition schema has a lore/description field so no draft carries lore). 9 rejected (FailedValidation microcopy misdirecting 5xx/429 — pre-existing 8.2/8.3 infra, already an 8.3 residual; worker-thread validator/registry reads — the established trigger/scenario pattern, registries immutable during authoring; triple `DrainEvents` per frame — harmless; no prompt-length guard — minor, mitigated by the token-budget patch; availability-gate TOCTOU — the generate path re-checks via `TryCreate` and is authoritative; ability id-collision asymmetry — Save-time `ConfirmOverwrite` protects; exact-message test assertion — the intentional four-state convention from 8.3; buildings prompt lacks a schema — buildings are optional and structurally validated by `FactionValidator`; `_faction.Units` NRE — the list is always initialized). 0 intent_gap (Intent Alignment confirmed the framework+panel B-reading and the A2 no-lore-field reading are the only schema-consistent readings), 0 bad_spec.

**Verification:** `dotnet build godot/godot.sln` → 0 errors (11 pre-existing warnings, none in touched regions). `dotnet test` full suite → 2929 passed, 1 skipped, 0 failed (39 EntityDraft tests: 35 original + 4 added in review). `grep "using Godot" src/AI/LLMService.cs` → none. Matrix Test Audit: all 12 I/O rows covered by tests that ran and passed. In-engine manual checks (the Godot-coupled editor panels — spinner, editable-draft landing, four-state availability line, graceful degradation) left for manual verification — outside the headless harness, the same boundary as 8.1/8.2/8.3.

**Follow-up review recommended:** false — this pass converged to localized hardening (3 medium + 1 low patches, no high, no intent/spec issue), each fully test-covered; the remaining gaps are the Godot-coupled UI seams the spec already designates manual-verified, filed as deferred work. Further independent passes have diminishing returns.

**Residual risks:**
- The Godot-coupled AI panel surfaces (prompt card, `ChimeraSpinner`, four-state availability line, editable-draft landing into each editor's form/pane, `Initialize`-before-`_Ready` ordering) are not unit-testable — correctness rests on compilation + the in-engine manual checks; the load-bearing Godot-free logic (generate pipeline, no-fallback/no-network, per-kind validation routing, quantize-by-reuse, prompt staleness, `MakeUniqueId` dedup) is fully Tier-1 covered.
- Faction ref-check depth: the draft ctx now supplies an `AbilityRegistry` (ability refs gated at draft time) but not `BehaviorRegistry`/`ItemRegistry`; behavior/item refs on a drafted faction unit are still deferred to the Finish gate (recorded as deferred work).
- The four-state failure voicing maps 5xx/429 to `FailedValidation` — inherited verbatim from 8.2/8.3, not a new decision here.
- A faction draft that exceeds even the 8192-token budget still truncates to a generic "Invalid JSON"; the budget is a heuristic, not a guarantee.
- `*.cs.uid` residual artifacts for the new test files are left untracked (repo convention, mirrors 8.1/8.2/8.3).
