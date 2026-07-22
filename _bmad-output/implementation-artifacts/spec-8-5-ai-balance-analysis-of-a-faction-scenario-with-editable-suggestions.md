---
title: 'Story 8.5 — AI balance analysis of a faction with editable, per-field suggestions'
type: 'feature'
created: '2026-07-21'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '424fb0f512dc65f1cb644f56a4ac2685bd372c1c'
final_revision: '4b8363936b313ec0b74656cba06fde2c6ff5f1c6'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-8-context.md'
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Epic 8 gives creators AI generation (triggers/maps in 8.3, entity drafts in 8.4) but no way to ask the AI to *critique* the balance of a faction they already authored. Designers want actionable, field-specific tuning suggestions they can review, edit, and apply — but only as editable data that passes the exact same validation gate as hand-authored stats, never auto-applied behind their back.

**Approach:** Add a provider-backed **balance-analysis** flow to the Godot-free `LLMService`, mirroring the 8.4 draft framework: `GenerateBalanceAnalysisAsync` over the shared `RunDraftAsync<T>` pipeline (own `CancellationTokenSource`, no-fallback, four-state, `StripMarkdown`, `DrainEvents` marshalling), an internal-static `BuildBalanceAnalysisPrompt` (staleness-guarded on the closed set of tunable fields), and a public-static `ValidateBalanceReport` router that parses the model output into an editable `BalanceReport` of per-field `BalanceSuggestion`s. Host the affordance in `UnitCardPanel` (the only panel bound to an existing faction's full roster). Applying a suggestion routes the proposed value through a new Godot-free `BalanceSuggestionApplier` that writes it onto a **clone** of the target unit and re-gates it with the **existing** `UnitDefinitionValidator` — quantization stays at the unchanged `EntityWorld.ApplyUnitDefinition` boundary, so an applied stat hashes identically to a hand-authored one. Nothing is auto-applied; the creator reviews/edits/discards each suggestion.

## Boundaries & Constraints

**Always:**
- `godot/src/AI/LLMService.cs` stays Godot-free (`grep "using Godot"` returns nothing) and reuses the 8.1–8.3 provider stack verbatim: `LlmProviderFactory.TryCreate` → `provider.GenerateAsync(NormalizedRequest)` → on unavailable/failure voice the four-state via `AiAvailabilityMessages.Describe` / `AiAvailabilityMap.FromFailure`; the key is read only via `ISecretStore`. The selected provider is authoritative with NO fallback. When no provider/key is configured, NO network request is made (`CallCount == 0`).
- A suggestion is applied only through the SAME `UnitDefinitionValidator.Validate(...)` gate hand-authored unit edits use. An out-of-Fixed-range (`[0, 32768)`), non-finite, negative-cost, or otherwise invalid proposed value is a **located** reject (field path + offending value); the target unit is left unchanged. Nothing is auto-applied — apply is an explicit per-suggestion action.
- The closed set of tunable field names is defined ONCE (in `BalanceSuggestionApplier`) and shared by the prompt builder (enumerates it), the validate router (rejects any `field` outside it), and the apply mapper — so the three cannot drift; a staleness-guard test fails if a member is missing from the prompt.
- Balance analysis operates on the faction's live in-memory roster (`_faction.Units`). Each `BalanceSuggestion` targets a specific existing unit id; a suggestion citing an unknown unit id or field is a located reject. Applied changes go through `UnitCardPanel`'s existing undo/validate/Save seam (`PushHistory` / `GoToUnit` / `RevalidateAndReflect` / `PersistSync`).
- Every AI affordance degrades gracefully: with AI unavailable the Analyze affordance disables and shows the four-state message while manual balance editing in the panel remains fully usable. The "Transmuting…" spinner shows during the analysis call. Microcopy addresses the creator as "Commander".

**Block If:**
- Applying a suggestion cannot reuse the existing `UnitDefinitionValidator` without modifying how *hand-authored* unit data is validated (would break the "same gate" invariant).

**Never:**
- No AI/LLM code in the sim tick; analysis is authoring-layer only. No new provider/adapter — reuse the 8.1–8.3 stack. No second float→Fixed quantization path and no new canonical hash over a bare definition.
- No auto-apply: a returned suggestion is never written to data without the creator's explicit per-suggestion apply, and only after passing the validator.
- **Scenario-level balance analysis is out of scope for this story.** No scenario-balance schema or field-editable scenario host exists (epic-8-context flags scenario-type as undefined; `MapGeneratorPanel` only *generates* `ScenarioData`, it does not edit per-field). Do NOT invent a scenario-type/scenario-balance schema unattended. Faction/unit balance (units + hero growth stats, the schema exercised by 8.4) is the buildable target; scenario balance is recorded as deferred work.
- Do not re-serialize faction files via reflection; persistence stays on the existing `FactionWriter`/`PersistSync` seam the panel already uses.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Analysis happy path | Configured provider + stub returns valid `{suggestions:[…]}` JSON; `GenerateBalanceAnalysisAsync(prompt, ctx, cb)` | Callback fires with a non-null `BalanceReport` whose suggestions each name an existing unit id + tunable field + proposed value + rationale; provider `CallCount == 1` | No error expected |
| Unparseable response | Provider returns prose / malformed JSON | `ValidateBalanceReport` returns `(null, located error)`; callback carries the four-state **FailedValidation**-style message; no data mutated | Located / four-state |
| Unknown field | A suggestion whose `field` is not in the closed tunable set | `ValidateBalanceReport` returns `(null, located error)` naming the offending `field` | Located reject |
| Unknown unit id | A suggestion citing a `unit_id` absent from `ctx` roster ids | `ValidateBalanceReport` returns `(null, located error)` naming the unit id | Located reject |
| Apply in-range | Valid suggestion, e.g. `attack_damage` 10→14; `BalanceSuggestionApplier.TryApply(target, suggestion, siblings)` | Returns `(candidate UnitDefinition, null)` with the field set; committing it and applying via `EntityWorld.ApplyUnitDefinition` yields the same SoA `Fixed` as an equivalent hand-authored unit | No error expected |
| Apply out-of-Fixed-range | Suggestion proposing `attack_damage: 40000` | `TryApply` returns `(null, located error)` naming the path + value from `UnitDefinitionValidator`; target unit unchanged | Located reject |
| Edited proposed value | Creator edits the proposed value before applying | `TryApply` gates the **edited** value: in-range applies, out-of-range located-rejects | Gate the edited value |
| No provider / no key | Provider unset or key absent; `GenerateBalanceAnalysisAsync` | Callback carries the distinct four-state message; NO network request (`CallCount == 0`) | Four-state message |
| Provider failure | Selected provider 401/unreachable | Callback carries the four-state message via `AiAvailabilityMap.FromFailure`; no other provider tried | Four-state message |
| Markdown-fenced response | Valid JSON wrapped in ```` ```json ```` fences | `StripMarkdown` unwraps it; report parses and reaches the callback | No error expected |
| Prompt staleness | A tunable-field member absent from `BuildBalanceAnalysisPrompt` | The prompt staleness-guard test fails | Test guard |

</intent-contract>

## Code Map

- `godot/src/AI/LLMService.cs` -- EDIT (primary). Add (1) `BalanceAnalysisContext` DTO (Godot-free, sealed: `IReadOnlyList<string> UnitIds` — the roster ids the router validates against; optional `IReadOnlyList<string> TunableFields` defaulted from `BalanceSuggestionApplier`). (2) `BalanceReport` + `BalanceSuggestion` DTOs (Godot-free, sealed; `BalanceSuggestion` = `UnitId`, `Field` (snake_case), `Proposed` (double), `Current` (double, advisory/display), `Rationale`). (3) `_balanceCts` field + `GenerateBalanceAnalysisAsync(string prompt, BalanceAnalysisContext ctx, Action<BalanceReport?, string?> onComplete)` thin wrapper over `RunDraftAsync<BalanceReport>` with its own CTS and a per-call `maxTokens` (analysis of a full roster needs a large budget — reuse the faction 8192 figure). (4) `internal static string BuildBalanceAnalysisPrompt(BalanceAnalysisContext ctx)` — states the task, the closed tunable-field vocabulary (one member per line, from `BalanceSuggestionApplier.TunableFields`, staleness-guardable), the JSON output schema, and "Return ONLY valid JSON. No markdown fences." (5) `public static (BalanceReport? report, string? error) ValidateBalanceReport(string json, BalanceAnalysisContext ctx)` — deserialize (located reject on malformed), reject any suggestion whose `Field` ∉ tunable set or whose `UnitId` ∉ `ctx.UnitIds`, with `JoinErrors`-style located messages.
- `godot/src/AI/BalanceSuggestionApplier.cs` -- NEW (Godot-free). `public static IReadOnlyList<string> TunableFields` — the single source of truth for the closed tunable-field set (unit numeric stats: `attack_damage, hp, armor, attack_range, attack_speed, splash_radius, vision_range, cost_ore, cost_crystal, supply, train_time, max_energy, collision_radius, mesh_scale, projectile_speed`, plus hero growth `hero.max_level/base_xp/xp_growth/xp_per_kill/xp_share_radius/health_per_level/damage_per_level/armor_per_level`). `public static (UnitDefinition? candidate, string? error) TryApply(UnitDefinition target, string field, double proposed, IReadOnlyList<UnitDefinition>? siblings)` — clone `target` (serialize via `FactionWriter.SerializeUnitClean` → deserialize with `FactionDefinition.JsonOptions`), set the field via a snake_case→setter switch keyed on `TunableFields`, run `new UnitDefinitionValidator().Validate(candidate, null, null, null, siblings, "unit")`, return `(candidate, null)` or `(null, JoinErrors(result.Errors))`. Unknown field → located error.
- `godot/src/Core/Definitions/UnitDefinition.cs` / `FactionDefinition.cs` -- READ/REUSE. Tunable stat props are plain settable `float`/`int`; `FactionDefinition.JsonOptions` (:184) is the lenient clone-deserialize options.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- READ/REUSE. `Validate(def, AbilityRegistry?, BehaviorRegistry?, ItemRegistry?, siblings, kind)` (:139) → located `[0,32768)`/finite/cost/hero range gate. The single sanctioned apply-time gate. Do NOT modify.
- `godot/src/Core/Definitions/FactionWriter.cs` -- READ/REUSE. `SerializeUnitClean(def)` (:195) for the deterministic clone. `EntityWorld.ApplyUnitDefinition` -- REFERENCE. The unchanged float→Fixed sim boundary.
- `godot/src/AI/Providers/*` -- REFERENCE/REUSE unchanged (`TryCreate`, `NormalizedRequest`/`NormalizedResult`, `AiAvailabilityMessages`, `AiAvailabilityMap`, `AiAvailabilityEvaluator.EvaluateConfig`).
- `godot/src/CreationSuite/UnitCardPanel.cs` (+ `.Edit.cs`) -- EDIT. Add a Balance-Analysis affordance distinct from the 8.4 draft card: prompt input, "Analyze ✦" button, `ChimeraSpinner` "Transmuting…", four-state `_aiAvailLabel` (reuse existing `RefreshAvailability`/`EvaluateConfig`), and an editable suggestion list (rows: field chip + rationale + editable proposed-value input + Apply/Discard). Build `BalanceAnalysisContext` from `_faction.Units` ids; call `GenerateBalanceAnalysisAsync`; on callback render rows. Apply → `BalanceSuggestionApplier.TryApply(liveTargetUnit, field, editedValue, _faction.Units)`; on success commit via `PushHistory`/`GoToUnit`/`RevalidateAndReflect`/`PersistSync`; on error show the located message, no mutation. Discard → drop the row. `DrainEvents()` already pumped in `_Process`.
- `godot/ProjectChimera.Sim.Tests/AI/` -- NEW test files (below), reusing `StubHttpMessageHandler`, `FakeSecretStore`, `Settings`, `AnthropicBody`, `Pump` from `EntityDraftTestData`/`LlmServiceRepointTests`.
- No phase changes: `UnitCardPhase.Initialize` already passes `LlmService`/`AiEvaluator`/`SecretStore` (8.4).

## Tasks & Acceptance

**Execution:**
- `godot/src/AI/BalanceSuggestionApplier.cs` -- NEW Godot-free `TunableFields` (single source of truth) + `TryApply(target, field, proposed, siblings)` that clones, sets by snake_case switch, and re-gates via the existing `UnitDefinitionValidator`, returning a candidate or a located error -- the testable apply-and-gate core; guarantees applied changes reuse the same gate + quantize boundary with no second path.
- `godot/src/AI/LLMService.cs` -- Add the `BalanceAnalysisContext`/`BalanceReport`/`BalanceSuggestion` DTOs, `_balanceCts`, `GenerateBalanceAnalysisAsync` (own CTS, roster-sized `maxTokens`), `BuildBalanceAnalysisPrompt` (tunable-field vocabulary from `BalanceSuggestionApplier.TunableFields`, staleness-guardable), and `ValidateBalanceReport` (located reject on malformed JSON / unknown field / unknown unit id); stays Godot-free, reuses the 8.3 no-fallback/four-state/StripMarkdown pipeline.
- `godot/src/CreationSuite/UnitCardPanel.cs` (+ `.Edit.cs`) -- Add the Balance-Analysis affordance (prompt, Analyze button, "Transmuting…" spinner, four-state label, editable suggestion rows with Apply/Discard) wired to `GenerateBalanceAnalysisAsync` and `BalanceSuggestionApplier.TryApply`, landing applied changes through the existing undo/validate/Save seam -- suggestions appear as editable data; nothing auto-applies; graceful degradation preserved.
- `godot/ProjectChimera.Sim.Tests/AI/BalanceAnalysisGenerationTests.cs` -- Stub happy path (non-null report, `CallCount==1`); NoProvider/NoKey short-circuit (four-state, `CallCount==0`); provider failure (four-state, no second attempt); fenced-JSON stripped -- proves the pipeline is consumed with no fallback and no network when unavailable.
- `godot/ProjectChimera.Sim.Tests/AI/BalanceAnalysisValidationTests.cs` -- `ValidateBalanceReport`: valid JSON → report; malformed/prose → located error; unknown `field` → located error; unknown `unit_id` → located error -- covers the parse/reject rows.
- `godot/ProjectChimera.Sim.Tests/AI/BalanceAnalysisApplyTests.cs` -- `BalanceSuggestionApplier.TryApply`: in-range value → candidate with field set; out-of-Fixed-range (`attack_damage:40000`) → located reject, original object unchanged; edited value gated (in-range applies, out-of-range rejects); quantize-by-reuse — an applied float, taken through `EntityWorld.Create`+`ApplyUnitDefinition`, yields the same SoA `Fixed` as an equivalent hand-authored unit -- proves apply reuses the existing gate/quantize boundary, not a second path.
- `godot/ProjectChimera.Sim.Tests/AI/BalanceAnalysisPromptTests.cs` -- Staleness guard: every member of `BalanceSuggestionApplier.TunableFields` heads its own line in `BuildBalanceAnalysisPrompt` (exact-line-token match, per the 8.3/8.4 `HeadsALine` style); the Fixed-safe range line is present -- prevents prompt drift from the tunable-field set.

**Acceptance Criteria:**
- Given a configured provider and a stub returning a valid suggestions payload, when `GenerateBalanceAnalysisAsync` runs, then its callback receives a non-null `BalanceReport` whose every suggestion names an existing roster unit id + a tunable field + a proposed value + a rationale, and provider `CallCount == 1`.
- Given an analysis response that does not parse into a structured report (malformed JSON, unknown field, or unknown unit id), when `ValidateBalanceReport` runs, then it returns `(null, located error)`, the callback carries the four-state failed-validation message, and no faction/unit data is mutated.
- Given a valid suggestion, when the creator applies it (optionally after editing the proposed value), then `BalanceSuggestionApplier.TryApply` gates the value through the same `UnitDefinitionValidator` hand-authored edits use and returns the updated candidate; a value outside the Fixed-safe range returns a located reject and the target unit is unchanged.
- Given an applied in-range suggestion, when the resulting unit is spawned via `EntityWorld.ApplyUnitDefinition`, then its SoA `Fixed` stat equals that of an equivalent hand-authored unit — no second quantize path or bare-definition hash is introduced.
- Given no provider or no key configured, when `GenerateBalanceAnalysisAsync` runs, then the callback carries the distinct four-state message and no network request is made; given a selected provider whose endpoint fails, the callback carries the four-state failure message and no other provider is invoked.
- Given a tunable-field member absent from `BuildBalanceAnalysisPrompt`, when the suite runs, then the prompt staleness-guard test fails.
- Given `godot/src/AI/LLMService.cs`, when inspected, then `grep "using Godot"` returns nothing and no second float→Fixed path or new bare-definition canonical hash is introduced.

## Design Notes

**Why UnitCardPanel hosts it, not FactionDefinerPanel.** Balance analysis is inherently cross-unit and operates on an *existing* faction. `UnitCardPanel` is the only creation-suite panel that binds a real faction (`_faction` with the full `Units` roster + write-back path) and exposes per-field editable controls keyed by the same JSON field names the validator uses as `FieldPath`s — so a suggestion tagged `"attack_damage"` maps 1:1 to a field with undo/badge/Save infrastructure already in place. `FactionDefinerPanel` only builds a brand-new draft from scratch (never binds an existing faction) and has no undo; it is unsuitable. No new phase wiring is needed — 8.4 already passes the AI deps into `UnitCardPhase`.

**Apply = clone → set → existing gate (the load-bearing decision).** The epic's "float quantized to Fixed by the SAME validation gate before persistence or any canonical hash" is honored by reuse. `TryApply` sets the proposed value onto a *clone* (so a rejected apply leaves the original untouched), runs the existing `UnitDefinitionValidator` (list-all located errors, `[0,32768)`/finite/cost/hero-growth range gate), and returns the candidate only if valid. Quantization then happens where hand-authored units already have it — `EntityWorld.ApplyUnitDefinition` (`Fixed.FromFloat`) — so an applied stat hashes identically to a hand-authored one by construction. No bespoke quantize step and no bare-definition hash.

**One closed vocabulary, three consumers.** `BalanceSuggestionApplier.TunableFields` is the single definition of which fields a suggestion may target. `BuildBalanceAnalysisPrompt` enumerates it (one per line), `ValidateBalanceReport` rejects anything outside it, and `TryApply`'s switch handles exactly it — a staleness-guard test fails if the prompt omits a member, preventing drift.

**Golden pipeline shape** (identical to `GenerateFactionDraftAsync`, `maxTokens` sized for a full roster):
```
GenerateBalanceAnalysisAsync(prompt, ctx, onComplete):
  _balanceCts?.Cancel(); _balanceCts = new();
  RunDraftAsync(BuildBalanceAnalysisPrompt(ctx),
                $"Analyze this faction for balance. Focus: {prompt}\n<serialized roster>",
                json => ValidateBalanceReport(json, ctx),
                _balanceCts.Token, onComplete, maxTokens: FACTION_DRAFT_MAX_TOKENS)
```

**UI is Godot-coupled (manual-verified), logic is Tier-1.** The panel affordance (prompt card, spinner, four-state line, editable suggestion rows, apply landing into the undo/Save seam) is not reachable by the headless harness — the same boundary 8.1–8.4 accepted. The load-bearing surface (generate pipeline, no-fallback/no-network, validate router, `TryApply` gate + quantize reuse, prompt staleness) is fully Tier-1 covered via `StubHttpMessageHandler`/`FakeSecretStore`/directly-constructed definitions.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including the new `AI/BalanceAnalysis{Generation,Validation,Apply,Prompt}Tests`; no prior test regresses.
- `dotnet build godot/godot.sln` -- expected: full solution compiles with the new service surface, the applier, and the panel affordance.
- `grep -n "using Godot" godot/src/AI/LLMService.cs godot/src/AI/BalanceSuggestionApplier.cs` -- expected: no matches (both stay Godot-free / Tier-1 + analyzer covered).

**Manual checks (in-engine, Godot-coupled UI not reachable by the headless harness):**
- Open the Unit Card editor (J) on a faction with a configured provider + key: request balance analysis; confirm the "Transmuting…" spinner shows, a list of per-field suggestions (field chip + rationale + editable proposed value) appears, editing a value then Apply updates the target unit and re-validates, an out-of-range Apply shows a located error and changes nothing, and Discard removes a suggestion without touching data.
- Clear the key: confirm the Analyze affordance disables and shows the four-state message while manual balance editing remains fully usable (graceful degradation).

## Spec Change Log

_No bad_spec loopback occurred; empty._

## Review Triage Log

### 2026-07-21 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 7: (high 0, medium 3, low 4)
- defer: 3
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` The load-bearing `SetField` snake_case→property switch (which decides *which* stat an applied suggestion mutates) had a right-property assertion for only 3 of 23 tunable fields — a wrong-property copy-paste (e.g. `vision_range`→`AttackRange`) would silently mutate + persist the wrong stat with every test green. Added data-driven `[Theory]` mapping tests over `BalanceSuggestionApplier.TunableFields` (15 unit + 8 hero) each asserting the *named* property reaches the applied value via an independent direct reader, plus a coverage-guard fact asserting every `TunableFields` member is handled by `SetField` (not `default`-rejected) — closing the drift between the accept-set (`ValidateBalanceReport`) and the apply-set. (Verification Gap / Edge Case Hunter — deduped)
  - `[medium]` `[patch]` `OnApplySuggestion` discarded `PersistSync()`'s bool and unconditionally toasted "…re-validated and saved", so a failed disk write showed a contradictory "saved" while `PersistSync` surfaced its own error and the in-memory mutation stood — every other panel save site guards the return. Now `if (!PersistSync()) return;` before the success toast, matching the panel convention. (Blind Hunter)
  - `[medium]` `[patch]` The undo/redo closure captured a raw list index (`_faction.Units[idx] = …`); the 8.4 draft-apply precedent deliberately uses reference-based restore because an index goes stale if the roster is later reordered/deleted, corrupting a different slot (or throwing) on undo. Switched to locate-by-reference (`IndexOf` at closure time) with idempotent guards. (Blind Hunter / Edge Case Hunter — deduped)
  - `[low]` `[patch]` The `I()` int-coercion guards (NaN→−1, over/underflow clamps) that deliberately force a validator reject instead of a silent NaN→0 apply were untested. Added `cost_ore`=NaN and `supply`=+Inf facts asserting a located reject with the original unchanged. (Verification Gap)
  - `[low]` `[patch]` The suggestion row displayed the model's unverified `current` claim (a hallucinated number, or "0.000" when omitted) and the apply toast echoed the un-rounded typed value while int fields round. Added a Godot-free, tested `BalanceSuggestionApplier.TryReadField` (the read counterpart of `SetField`); the row now shows the unit's REAL current value and the toast echoes the value actually committed. (Blind Hunter)
  - `[low]` `[patch]` The `BalanceSuggestionApplier` class comment claimed a staleness-guard test enforces `SetField` coverage, but the only guard checked the prompt builder. Corrected the comment to name both real guards (now that the `SetField` coverage-guard test exists). (Verification Gap)
  - `[low]` `[patch]` An empty `suggestions:[]` was reported as "the roster reads balanced, Commander" — a model that simply failed to produce suggestions is indistinguishable from a genuine all-clear, a false-confidence signal in a balance tool. Reworded to the non-committal "No tuning suggestions returned, Commander." (Blind Hunter)

## Auto Run Result

Status: done

**Summary:** Added a provider-backed **balance-analysis** flow to the Godot-free `LLMService`, mirroring the Story 8.4 draft framework: `GenerateBalanceAnalysisAsync` (own `CancellationTokenSource`) over the shared `RunDraftAsync<T>` pipeline (snapshot settings → `LlmProviderFactory.TryCreate` four-state + no-request short-circuit → `provider.GenerateAsync` → `!Ok` four-state via `AiAvailabilityMap.FromFailure` → `StripMarkdown` → validate → marshal via `DrainEvents`), an internal-static `BuildBalanceAnalysisPrompt` (staleness-guarded on the closed tunable-field vocabulary + roster ids + Fixed-safe range), and a public-static `ValidateBalanceReport` that parses model output into an editable `BalanceReport` of per-field `BalanceSuggestion`s, located-rejecting malformed JSON / unknown field / unknown unit id. A new Godot-free `BalanceSuggestionApplier` is the single source of truth for the closed tunable-field set and applies a suggestion by cloning the target unit (via `FactionWriter.SerializeUnitClean` round-trip), setting the field through a snake_case→setter switch, and re-gating the **clone** through the **existing** `UnitDefinitionValidator` — so a rejected value never touches the original and quantization stays at the unchanged `EntityWorld.ApplyUnitDefinition` boundary (an applied stat hashes identically to a hand-authored one). `UnitCardPanel` (the only panel bound to an existing faction's full roster) gained a Balance-Analysis affordance: focus prompt, "Analyze ✦" button, `ChimeraSpinner` "Transmuting…", four-state availability line, and editable per-field suggestion rows (real current value, editable proposed value, Apply/Discard) that commit an applied change through the existing undo/validate/Save seam — nothing auto-applies. No phase wiring was needed (8.4 already passes the AI deps into `UnitCardPhase`).

**Files changed:**
- `godot/src/AI/BalanceSuggestionApplier.cs` — NEW Godot-free apply-and-gate core: `TunableFields` (single source of truth: 15 unit stats + 8 hero growth), `TryApply` (clone → set → re-gate through existing validator), and `TryReadField` (read counterpart, added in review P6).
- `godot/src/AI/LLMService.cs` — `GenerateBalanceAnalysisAsync` (`_balanceCts`, roster-sized 8192 `maxTokens`), `BuildBalanceAnalysisPrompt`, `ValidateBalanceReport`, and the `BalanceAnalysisContext`/`BalanceReport`/`BalanceSuggestion` DTOs. Stays Godot-free.
- `godot/src/CreationSuite/UnitCardPanel.cs` — the Balance-Analysis affordance; apply routes through `BalanceSuggestionApplier.TryApply` and commits via `PushHistory`/`GoToUnit`/`RevalidateAndReflect`/`PersistSync`. Review patches: guarded `PersistSync` return (P4), reference-based undo (P5), real current + accurate toast via `TryReadField` (P6), non-committal empty message (P7).
- `godot/ProjectChimera.Sim.Tests/AI/BalanceAnalysis{Generation,Validation,Apply,Prompt}Tests.cs` — the Tier-1 suite (48 tests: 22 original + 26 added in review for per-field mapping coverage, SetField coverage guard, and int non-finite rejects).

**Review findings:** 7 patches applied (medium 3, low 4 — see Review Triage Log). 3 deferred (validator's permissive `0` lower bound lets degenerate values like `attack_speed=0` commit — pre-existing, affects all authoring; the per-kind in-flight cancellation apparatus can repaint rows from a superseded run — pre-existing draft-flow pattern mirroring the 8.4 deferral; the tunable set omits movement `speed`/`xp_bounty` while including cosmetic `mesh_scale` — a product-vocabulary decision, `speed` excluded because it quantizes at the `Create` ctor outside this story's `ApplyUnitDefinition`-reuse contract). 9 rejected (`ValidateBalanceReport` doesn't range-check `proposed` — by-design apply-time gate per the I/O matrix; busy-toggle re-enables the Analyze button — the generate path re-checks authoritatively via `TryCreate`, rejected identically in 8.4; AC3 located-error vs four-state — the located parse error is a clear non-corrupting failed-validation surface, four-state reserved for provider availability per the 8.2–8.4 convention; `_faction.Units` null NRE — roster initialized on every real bind path, 8.4-adjudicated; transient duplicate-id blocks apply — correct, an invalid roster state should block with a located message; duplicate suggestions, redundant `ClearBalanceRows`, CTS non-disposal, stale sibling rows — subsumed/low/pre-existing-pattern). 0 intent_gap (scenario-level balance is authorized out-of-scope by the intent's own artifacts — FR-33 ties target data to the entity/faction definition schemas and epic-8-context flags scenario-type as undefined/deferred), 0 bad_spec.

**Verification:** `dotnet build godot/godot.sln` → 0 errors (11 pre-existing warnings, none in touched files). `dotnet test` full suite → 2977 passed, 1 skipped, 0 failed (48 BalanceAnalysis tests). `grep "using Godot" src/AI/LLMService.cs src/AI/BalanceSuggestionApplier.cs` → none. Matrix Test Audit: all 11 I/O rows covered by tests that ran and passed. In-engine manual checks (the Godot-coupled `UnitCardPanel` affordance — spinner, editable suggestion rows, apply landing, four-state availability line, graceful degradation) left for manual verification — outside the headless harness, the same boundary as 8.1–8.4.

**Follow-up review recommended:** false — this pass converged to localized hardening (3 medium + 4 low patches, no high, no intent/spec issue), each fully test-covered (the two load-bearing patches, `SetField` mapping and the undo/persist safety, are now Tier-1 guarded); the remaining items are the Godot-coupled UI seams the spec already designates manual-verified, filed as deferred work. Further independent passes have diminishing returns.

**Residual risks:**
- The Godot-coupled `UnitCardPanel` balance affordance (prompt card, `ChimeraSpinner`, four-state availability line, editable suggestion rows, the apply→commit landing through the undo/Save seam) is not unit-testable — correctness rests on compilation + the in-engine manual checks; the load-bearing Godot-free logic (generate pipeline, no-fallback/no-network, validate router, `TryApply` gate + quantize reuse, per-field `SetField`/`TryReadField` mapping, prompt staleness) is fully Tier-1 covered.
- Scenario-level balance analysis is intentionally out of scope (no scenario-balance schema or field-editable scenario host exists) — recorded as deferred work, not implemented.
- The validator's `[0, 32768)` lower bound admits `0` for stats like `attack_speed`, so a one-click apply can reach a degenerate-but-valid unit; this is the same gate hand-authored data uses (a pre-existing property, deferred).
- `*.cs.uid` residual artifacts for the new files are left untracked (repo convention, mirrors 8.1–8.4).
