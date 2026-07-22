---
title: 'Story 8.3 — AI generation on the provider stack: repoint LLMService, extend trigger prompt/validator for new DSL constructs, parameterize map clamps'
type: 'feature'
created: '2026-07-21'
status: 'done'
baseline_revision: '3dd9a3fbeac2b82410f81d7c4243f1138205d3ea'
final_revision: 'd8aba0006808e8e114580beda24201990368c30d'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-8-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-8-2-godot-free-illmprovider-anthropic-ollama-openrouter-four-state-availability-ui-test-connection.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** AI trigger and map generation still run through `LLMService`'s two hardcoded HTTP methods (`TryClaudeAsync`→`TryOllamaAsync`) with an implicit Claude→Ollama fallback and a plaintext `AnthropicApiKey` property — Story 8.2 built the `ILLMProvider` stack (no-fallback, host-allowlisted, response-capped, secret-store keyed) but nothing consumes it. Separately, the trigger-generation prompt/validator is stale against the DSL vocabulary earlier epics added (5 flat event kinds + `unit_in_region` are omitted from the prompt; the LLM `Validate` gate checks **no** construct membership, so an unknown construct slips through with only a generic error), and the map-generation validator hardcodes RTS-only clamps (≥2 player slots, forced faction-JSON paths, ≤6 combat units/slot) that wrongly reject non-RTS scenarios.

**Approach:** Repoint both `GenerateTriggerAsync` and `GenerateScenarioAsync` onto `ILLMProvider` via `LlmProviderFactory.TryCreate` (authoritative selected provider, no fallback), deleting `TryClaudeAsync`/`TryOllamaAsync`/`AnthropicApiKey`/the URL consts and giving `LLMService` an `AllowAutoRedirect=false` client. Extend the flat trigger prompt to enumerate every flat `NodeKinds` construct, and add located unknown-construct rejection to `LLMService.Validate` by checking event/condition/action `Type` membership against `NodeKinds`. Parameterize the three RTS clamps out of `ValidateScenario`/`BuildMapSystemPrompt` into the trusted `MapGeneratorContext` (RTS defaults = today's literals, so RTS output is unchanged; a non-RTS caller supplies relaxed values). Universal position/spacing/bounds checks always run; nothing is sourced from the untrusted scenario file.

## Boundaries & Constraints

**Always:**
- The repoint routes every generation call through `LlmProviderFactory.TryCreate(getSettings(), secretStore, http, out provider, out failure)` then `provider.GenerateAsync(NormalizedRequest, ct)`. The selected provider is authoritative: on failure the generation callback surfaces that provider's error and **no other provider is attempted**.
- `LLMService` reads the key **only** through `ISecretStore.Get(SecretIds.Llm)` (via the factory) — never an `AnthropicApiKey`/`[Export]` field, `SettingsData`, or a literal. The `AnthropicApiKey` property, `TryClaudeAsync`, `TryOllamaAsync`, `CLAUDE_URL`, `OLLAMA_URL`, and the implicit-fallback lines are deleted.
- `LLMService`'s `HttpClient` is constructed with `AllowAutoRedirect = false` (a real key now flows through it; this closes the same redirect-key-leak 8.2 fixed for the evaluator's client).
- `LLMService` stays Godot-free (no `using Godot;`, lands under `godot/src/AI/` so `SimSources.props` keeps it Tier-1 + analyzer covered). Provider construction dependencies are injected as Godot-free seams — a settings accessor (`Func<SettingsData>` or equivalent), `ISecretStore`, and an injectable `HttpClient` — so the repointed generate path and both validators are unit-testable against stub endpoints with no live network.
- `LLMService.Validate` (trigger gate) rejects any event/condition/action whose `Type` is not in the corresponding `NodeKinds` flat set, with a **located** error naming the path and offending value (e.g. `triggers[0].actions[1].type='foo' is not a known trigger action type.`) — matching the message shape `ScenarioValidator` already uses.
- The flat trigger prompt (`BuildSystemPrompt`) enumerates every member of `NodeKinds.EventTypes`, `NodeKinds.ConditionTypes`, and `NodeKinds.FlatActionTypes` — including the previously-omitted `unit_damaged`/`unit_trained`/`ability_cast`/`hero_level`/`player_chat` events and the `unit_in_region` condition. A staleness-guard test fails if a future flat construct is added to `NodeKinds` but not described in the prompt.
- The three map clamps are parameters on the **trusted** `MapGeneratorContext` (min player slots, max combat units per slot, per-slot faction-JSON resolution), defaulting to today's values (2, 6, and the existing slot-0/slot-1 mapping). `BuildMapSystemPrompt` reflects the same parameter values it validates against.
- Universal checks — schema deserialize, position/map-bounds, ore-node spacing — always run for every scenario regardless of clamp values. A generated map is returned to the panel only after `ValidateScenario` passes.
- Any generation/callback marshalling to the Godot main thread keeps using the existing `ConcurrentQueue`/`DrainEvents()` pattern (callbacks still fire on the main thread via the panels' pump).

**Block If:**
- `LlmProviderFactory.TryCreate`, `NormalizedRequest`/`NormalizedResult`, `SettingsData.Llm*`, or `ISecretStore`/`SecretIds` (Story 8.1/8.2 frozen surfaces) would have to change **incompatibly** to consume them — the repoint must fit them as-is.

**Never:**
- Do NOT introduce a persisted `ScenarioType`/`MapType`/`GameMode` schema, enum, or registry — the epic flags "scenario type" as an undefined cross-story gap requiring a decision. The clamps are parameterized via the trusted `MapGeneratorContext`, and the relaxed values are **never** read from the parsed (untrusted) scenario file (that would weaken the gate circularly). A future `ScenarioType` registry is deferred (see Design Notes).
- Do NOT move trigger generation onto the graph IR (`trigger_graph` node/edge form) to emit graph-only constructs (custom events, `for_each`, expression nodes, `order_units`, objective leaves, etc.). This story extends the existing **flat** `TriggerDefinition` generation path; graph-only constructs are covered only by the located-rejection safety net (any unknown/graph-only construct in flat output is rejected). Graph-IR generation is out of scope (a separate, epic-sized effort).
- Do NOT add a scenario-type selection UI, and do NOT modify the `ILLMProvider` adapters, the availability evaluator, `LlmProviderFactory`, or the four-state UI built in 8.2.
- No vendor SDK / NuGet package; no streaming. No sim-layer coupling (provider layer is string-in/string-out; generated numerics still quantize via the existing `FixedJsonConverter` at deserialize).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Repoint happy path | Provider configured + reachable stub, valid JSON body | Configured adapter is called; validated `TriggerDefinition`/`ScenarioData` returned to callback | n/a |
| No fallback | Configured provider's stub returns 500/throws | Callback error = that provider's failure; NO other provider adapter invoked | Failure surfaced, not masked |
| Unavailable (no provider/key) | Factory returns `false` + `NoProvider`/`NoKey` | Callback error = the four-state message; no network request | Config-derived, synchronous |
| Trigger unknown construct | Generated trigger uses an event/condition/action `Type` not in `NodeKinds` | `Validate` returns null + a located error (path + offending value) | Rejected at the LLM gate, not just the load gate |
| Trigger new flat construct | Generated trigger uses `unit_damaged` / `unit_in_region` | `Validate` accepts it (member of `NodeKinds`) | n/a |
| Prompt staleness | A flat construct exists in `NodeKinds` but not in the prompt text | Staleness-guard test fails | Compile/test-time guard |
| Map RTS default (no regression) | Default `MapGeneratorContext`; scenario with 7 combat units in a slot, or 1 player slot | Rejected exactly as today (max 6 / min 2) | Located reject message |
| Map relaxed | `MapGeneratorContext` with min slots=1 and max combat=20; scenario with 1 slot + 10 combat units, valid positions/spacing | `ValidateScenario` passes; scenario returned | n/a |
| Map universal still fires under relaxed | Relaxed context; scenario with an out-of-bounds unit or ore nodes <15u apart | Rejected (position/spacing/bounds run regardless) | Located reject message |
| Forced faction path per slot | Scenario with hallucinated `faction_json`; N player slots | Each slot's `FactionJson` overwritten from the trusted per-slot context resolution (RTS 2-slot default identical to today) | n/a |

</intent-contract>

## Code Map

- `godot/src/AI/LLMService.cs` -- EDIT (primary). Namespace `ProjectChimera.AI`, Godot-free POCO. (1) Ctor: inject a settings accessor + `ISecretStore` + optional `HttpClient` (test seam); build the owned client with `AllowAutoRedirect=false`. (2) `GenerateTriggerAsync` (:101) and `GenerateScenarioAsync` (:440): replace the `TryClaude→TryOllama` block (:119-125 / :458-462) with `LlmProviderFactory.TryCreate(...)` → `provider.GenerateAsync(new NormalizedRequest(systemPrompt, userMessage, maxTokens), token)`; map `result.Ok ? StripMarkdown(result.Text) : null` and `result.Error`. (3) Delete `AnthropicApiKey` (:84), `TryClaudeAsync` (:165), `TryOllamaAsync` (:212), `CLAUDE_URL`/`OLLAMA_URL` consts. (4) `Validate` (:258): add located `Type`-membership checks against `NodeKinds.EventTypes`/`ConditionTypes`/`FlatActionTypes`. (5) `BuildSystemPrompt` (:329): enumerate every flat `NodeKinds` construct (add the 5 omitted events + `unit_in_region`). (6) `MapGeneratorContext` (:35) gains `MinPlayerSlots`/`MaxCombatUnitsPerSlot` + per-slot faction-path resolution (RTS defaults 2/6 + existing slot-0/1 mapping). (7) `ValidateScenario` (:503) reads clamp params from the context (:520 `<MinPlayerSlots`, :524-529 forced-path resolution, :587 `>MaxCombatUnitsPerSlot`); universal Passes 1/5/6 unchanged. (8) `BuildMapSystemPrompt` (:595) states the context's clamp values (:628/:632).
- `godot/src/Dsl/NodeBase.cs` -- READ/REUSE. `internal static class NodeKinds` (:559) — the closed vocabulary. `EventTypes` (:613), `ConditionTypes` (:626), `FlatActionTypes` (:638). `internal` is same-assembly (`ProjectChimera`), so `LLMService` can reference it. Source of truth for both the prompt enumeration and the `Validate` membership check. Do not edit.
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` -- EDIT (:25). Construct `LLMService` with the new ctor (`() => _ctx.SettingsMgr.Current`, `_ctx.SecretStore`, and a `AllowAutoRedirect=false` client) instead of `{ AnthropicApiKey = ... }`. Remove the stale "Empty ⇒ Ollama fallback" comment (:24) and the "(Ollama fallback)" status text (:67) — there is no fallback now.
- `godot/src/Core/Bootstrap/Phases/MapGeneratorPhase.cs` -- VERIFY. Reuses `_ctx.LlmService`; confirm it still compiles and the default `MapGeneratorContext` it passes preserves RTS behavior. Change only if it constructs the context with now-parameterized fields.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` / `MapGeneratorPanel.cs` -- VERIFY. Callback signatures (`Action<TriggerDefinition?, string?>` / `Action<ScenarioData?, string?>`) are unchanged, so the review/edit-before-apply flow (preview → Accept/Load/Save) should need no change; confirm.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- REFERENCE. `SettingsMgr` (:111), `SecretStore` (:115), `LlmService` (:154) already present; no field add expected (LLMService owns its client).
- `godot/ProjectChimera.Sim.Tests/AI/` -- NEW test files (below). Reuse the existing `StubHttpMessageHandler` + `FakeSecretStore` helpers 8.2 added here.

## Tasks & Acceptance

**Execution:**
- `godot/src/AI/LLMService.cs` -- Repoint both generate methods onto `ILLMProvider` via the factory; delete the two adapters + `AnthropicApiKey` + URL consts + fallback; `AllowAutoRedirect=false` client; inject settings/secret/http seams -- consumes the 8.2 stack with the authoritative no-fallback contract, keyed only through the secret store.
- `godot/src/AI/LLMService.cs` -- Add located `NodeKinds` membership rejection to `Validate`; enumerate all flat constructs in `BuildSystemPrompt` -- unknown constructs rejected with a located error at the LLM gate; prompt no longer omits flat DSL vocabulary.
- `godot/src/AI/LLMService.cs` -- Parameterize the three clamps into `MapGeneratorContext` (RTS defaults); read them in `ValidateScenario` and reflect them in `BuildMapSystemPrompt`; keep universal passes intact -- non-RTS scenarios stop being wrongly rejected while RTS output is byte-for-byte unchanged and the gate never trusts the scenario file.
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` -- New `LLMService` construction + remove stale fallback messaging -- one wiring site; no plaintext key; honest status text.
- `godot/ProjectChimera.Sim.Tests/AI/LlmServiceRepointTests.cs` -- Repoint + no-fallback + unavailable: configured provider used against a stub; a failing provider yields no second attempt; `NoProvider`/`NoKey` short-circuit with the four-state message and no request (drive the async path via the existing `DrainEvents()` pump) -- proves the stack is consumed and the fallback is gone.
- `godot/ProjectChimera.Sim.Tests/AI/LlmTriggerValidatorTests.cs` -- `Validate` accepts the newly-added flat constructs (`unit_damaged`, `unit_in_region`); rejects an unknown event/condition/action `Type` with a located error (asserts path + offending value); `BuildSystemPrompt` mentions every member of `NodeKinds.EventTypes`/`ConditionTypes`/`FlatActionTypes` (staleness guard) -- covers the trigger prompt/validator extension.
- `godot/ProjectChimera.Sim.Tests/AI/LlmScenarioClampTests.cs` -- Default context rejects >6 combat / <2 slots (no regression); a relaxed context accepts them; a relaxed context still rejects out-of-bounds positions and <15u ore spacing (universals fire regardless); forced faction-path resolution overwrites hallucinated paths per slot; `BuildMapSystemPrompt` reflects the context's clamp values -- covers the map-clamp parameterization end-to-end.

**Acceptance Criteria:**
- Given each provider selected in `SettingsData` in turn and a stub endpoint, when `GenerateTriggerAsync`/`GenerateScenarioAsync` run, then the configured provider's adapter is used and a validated draft (or the provider's error) reaches the callback — and `LLMService` contains no `TryClaudeAsync`/`TryOllamaAsync`/`AnthropicApiKey`/`CLAUDE_URL`/`OLLAMA_URL` and no implicit fallback.
- Given a selected provider whose endpoint fails, when generation runs, then the callback reports that provider's failure and no other provider's adapter is invoked.
- Given no provider configured or no key, when generation runs, then the callback carries the distinct four-state message and no network request is made; the key, when present, is read only via `ISecretStore` (never a property/`[Export]`/settings).
- Given a generated trigger whose event/condition/action `Type` is outside `NodeKinds`, when `Validate` runs, then it returns null with a located error naming the path and offending value; given one using `unit_damaged` or `unit_in_region`, it validates successfully.
- Given a flat construct present in `NodeKinds` but absent from `BuildSystemPrompt`, when the suite runs, then the prompt staleness-guard test fails.
- Given the default (RTS) `MapGeneratorContext`, when `ValidateScenario` runs on a scenario exceeding the RTS clamps, then it is rejected exactly as before this change; given a `MapGeneratorContext` with relaxed clamps and an otherwise-valid non-RTS scenario, then it passes — while out-of-bounds positions and sub-15u ore spacing are still rejected under the relaxed context, and no clamp value is sourced from the scenario file.
- Given `LLMService`'s `HttpClient`, when inspected, then it is constructed with `AllowAutoRedirect = false`; and `grep "using Godot" godot/src/AI/LLMService.cs` returns nothing.

## Design Notes

**Deferred: a real `ScenarioType` registry.** The epic wants clamps relaxed "so non-RTS scenario types are not wrongly rejected," but no story defines a scenario-type schema, and the epic explicitly forbids sourcing relaxed limits from the untrusted scenario file (circular validation) and says to flag it for a decision. The only trusted carrier already threaded into both `BuildMapSystemPrompt` and `ValidateScenario` is `MapGeneratorContext` (editor/caller-supplied). So this story parameterizes the clamps into that trusted context with RTS-preserving defaults — the mechanism and the RTS "preset" ship and are proven; a future story can add a `ScenarioType` enum + per-type preset table + selection UI that populates the context. This is a genuine, verifiable slice (relaxed context ⇒ previously-rejected scenario passes; default context ⇒ identical behavior), not inert. Add a `deferred-work.md` note recording the `ScenarioType` decision.

**Trigger scope: flat, not graph.** The prompt speaks the flat `TriggerDefinition` schema; the omitted constructs it *can* teach are the flat ones (5 events + `unit_in_region`). Epic-7 graph-only constructs would require regenerating onto the graph IR — out of scope. The safety contract ("unknown construct rejected with a located error") is still met for *all* constructs: `Validate` now rejects anything outside the flat `NodeKinds` sets with a located error (the graph parse gate `NodeBaseJsonConverter` already does the same for the graph channel). Drive `Validate`'s membership + the prompt enumeration off `NodeKinds` so both stay in lockstep with the registry.

**No-fallback shape.** Old flow was "call Claude if key present, then Ollama if json still null." New flow: `TryCreate` → if `false`, callback with `AiAvailabilityMessages.Describe(failure)`; else `GenerateAsync`, and on `!result.Ok` callback with the four-state message via the shared `AiAvailabilityMap.FromFailure(result.Failure)` (review patch — so the async failure path is voiced identically to Test-connection, not with a raw adapter string). `NormalizedResult` maps onto the existing `(string? json, string? error)` tuple with no shape change to callers.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including the new `AI/LlmServiceRepointTests`, `AI/LlmTriggerValidatorTests`, `AI/LlmScenarioClampTests`; no prior test regresses.
- `dotnet build godot/godot.sln` -- expected: full project compiles with the repointed `LLMService` + phase wiring.
- `grep -n "using Godot" godot/src/AI/LLMService.cs` -- expected: no matches (stays Godot-free / Tier-1 + analyzer covered).
- `grep -nE "TryClaudeAsync|TryOllamaAsync|AnthropicApiKey|CLAUDE_URL|OLLAMA_URL" godot/src/AI/LLMService.cs` -- expected: no matches (the hardcoded adapters + fallback + plaintext key are gone).

**Manual checks (in-engine, Godot-coupled UI not reachable by the headless harness):**
- Open the Trigger Editor with a configured provider + key: generate a trigger from natural language; confirm the preview/Accept flow still appends to the scenario, and that with the key cleared, generation reports the no-key/unavailable state (no silent Ollama fallback).
- Open the Map Generator with a configured provider: generate an RTS map; confirm it validates/loads as before (no regression), and the Save path still runs `MapWriteGate.Check`.

## Review Triage Log

### 2026-07-21 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 4
- reject: 6
- addressed_findings:
  - `[medium]` `[patch]` Runtime provider failures on both generate paths surfaced the raw adapter string (e.g. `Anthropic API error 401: {…}`) verbatim, bypassing the four-state availability UX the epic mandates — the synchronous NoProvider/NoKey path used the polished "Commander, …" microcopy while the async 401/unreachable half did not. Added a shared `AiAvailabilityMap.FromFailure(NormalizedFailure)` (single source), routed `AiAvailabilityEvaluator.TestConnectionAsync` through it, and voiced both `LLMService` generate-failure sites with `AiAvailabilityMessages.Describe(...)` so the async failure path classifies identically to Test-connection; updated the two repoint no-fallback tests to assert the exact mapped message (they previously asserted only non-empty, codifying the raw-error behavior). (Blind Hunter)
  - `[medium]` `[patch]` The owned-client `AllowAutoRedirect=false` hardening (an AC of this spec, and the fix for the cross-host `x-api-key` redirect leak) was unverified — every test injects an explicit `HttpClient`, so the `http:null` owned branch never ran and no test asserted the property. Extracted the handler construction to an `internal static LLMService.BuildOwnedHttpHandler()` and added a Tier-1 test asserting `AllowAutoRedirect == false`. (Verification Gap)
  - `[low]` `[patch]` `StripMarkdown` was moved to the `Validate`/`ValidateScenario` call sites but no test fed a ```json-fenced body through the repointed path, so a dropped strip would not fail any test (and the param lost the prior `?? ""` null guard). Made `StripMarkdown` null-safe and added a fenced-response repoint test asserting the fenced JSON still validates. (Verification Gap + Edge Case Hunter)
  - `[low]` `[patch]` The prompt staleness guard used substring `Assert.Contains`, which false-passes on a collision (the `unit_count` condition is a substring of the `unit_count_threshold` event line, so deleting the `unit_count` line would stay green). Rewrote it to an exact line-token match (each construct must head its own description line). Also dropped a dead `stub` parameter from the repoint test helpers. (Blind Hunter)

## Auto Run Result

Status: done

**Summary:** Repointed `LLMService`'s trigger and map generation onto the Story 8.2 `ILLMProvider` stack via `LlmProviderFactory.TryCreate` — the selected provider is authoritative with NO Claude→Ollama fallback, the key is read only through `ISecretStore`, and the owned `HttpClient` is hardened `AllowAutoRedirect=false`. Deleted `TryClaudeAsync`/`TryOllamaAsync`/`AnthropicApiKey`/`CLAUDE_URL`/`OLLAMA_URL` and the implicit fallback. Added a located `NodeKinds`-membership pass to the trigger `Validate` gate (unknown AND graph-only constructs rejected with a path+value error, driven off the same registry the load gate aliases), extended the flat trigger prompt with the 5 previously-omitted Story-7.13 events + the `unit_in_region` condition (guarded by a staleness test), and parameterized the three RTS map clamps (`MinPlayerSlots`, `MaxCombatUnitsPerSlot`, per-slot `FactionJsonResolver`) onto the trusted `MapGeneratorContext` with RTS-preserving defaults — universal position/spacing/bounds passes always run; no clamp is ever sourced from the untrusted scenario file.

**Files changed:**
- `godot/src/AI/LLMService.cs` — the repoint (both generate methods → provider stack, no fallback, `AllowAutoRedirect=false` owned client via `BuildOwnedHttpHandler`), the located `NodeKinds` membership pass, the extended flat prompt, and the clamp parameterization; failure sites voiced with the shared four-state map; `StripMarkdown` null-safe.
- `godot/src/AI/Providers/AiAvailability.cs` — added `AiAvailabilityMap.FromFailure` (single failure→state source).
- `godot/src/AI/Providers/AiAvailabilityEvaluator.cs` — `TestConnectionAsync` now routes through `AiAvailabilityMap.FromFailure` (behavior-preserving; shared with the generate path).
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` — constructs `LLMService` via the new ctor (settings accessor + secret store; owned hardened client); removed the stale "Ollama fallback" comment/status text.
- `godot/ProjectChimera.Sim.Tests/AI/LlmServiceRepointTests.cs` — repoint happy-path / no-fallback (`CallCount==1`, now asserting the mapped four-state message) / NoProvider-NoKey short-circuit (`CallCount==0`) / key-from-secret-store / owned-handler-refuses-redirects / fenced-JSON-stripped.
- `godot/ProjectChimera.Sim.Tests/AI/LlmTriggerValidatorTests.cs` — accepts new flat constructs; located rejection of unknown + graph-only (`order_units`/`custom_event`); exact-line-token staleness guard.
- `godot/ProjectChimera.Sim.Tests/AI/LlmScenarioClampTests.cs` — default rejects >6/<2, relaxed accepts, universals still fire under relaxed, forced per-slot faction-path overwrite, prompt reflects clamp values.
- `_bmad-output/implementation-artifacts/deferred-work.md` — the deferred `ScenarioType` decision + 4 review-deferred items.

**Review findings:** 4 patches applied (medium 2, low 2 — see Review Triage Log). 4 deferred (non-2-slot clamp/prompt/resolver completeness; slot-index uniqueness/range; `player_chat` registered-but-never-raised; `LLMService` non-disposal). 6 rejected (injected-client redirect is a test-only seam / production injects null; settings deep-copy benign; mixed error grammars cosmetic — Pass 2 deliberately matches the load gate; empty-Ok-body reclassification; UI-gated pre-existing stale-callback; doc overstatement). 0 intent_gap (Intent Alignment confirmed the flat-A1 + trusted-parameterization-B1 readings are epic-aligned, not gaps), 0 bad_spec.

**Verification:** `dotnet build godot/godot.sln` → 0 errors (11 pre-existing warnings, none in touched files). `dotnet test` full suite → 2890 passed, 1 skipped, 0 failed. `grep "using Godot" src/AI/LLMService.cs` → none. `grep -E "TryClaudeAsync|TryOllamaAsync|AnthropicApiKey|CLAUDE_URL|OLLAMA_URL"` → none. Matrix Test Audit: all 10 I/O rows covered by tests that ran and passed. In-engine manual checks (Trigger/Map generator Godot-coupled UI, live provider round-trip) left for manual verification — outside the headless harness, same boundary as 8.1/8.2.

**Follow-up review recommended:** false — this pass converged to localized hardening (medium/low only, no high, no intent/spec issue), fully test-covered; the four-state mapping reuses 8.2's existing Test-connection logic (a consistency change, not a new decision) and the evaluator refactor is behavior-preserving. Further independent passes have diminishing returns.

**Residual risks:**
- The Godot-coupled surfaces (Trigger/Map generator panels' preview-Accept/Load-Save flow, the `TriggerEditorPhase` settings-accessor wiring, the owned no-redirect handler in production) are not unit-testable — correctness rests on compilation + the in-engine manual checks; the load-bearing Godot-free logic (repoint, no-fallback, membership gate, clamp parameterization, four-state mapping) is fully Tier-1 covered.
- The four-state failure voicing maps a 401 (rejected key) to `FailedValidation` — inherited verbatim from 8.2's Test-connection mapping, not a new decision here; a future refinement could add an auth-specific state.
- The clamp relaxation is a capability with no non-RTS caller wired yet (per the parked `ScenarioType` decision); its non-2-slot completeness gaps (prompt example, default resolver, clamp lower-bounds) and the pre-existing slot-index-uniqueness hole are in `deferred-work.md`.
- Removing the implicit Ollama fallback is an intended behavior change: a user who relied on empty-key→Ollama must now explicitly select the `ollama` provider.
- `*.cs.uid` residual artifacts for the new test files are left untracked (repo convention, mirrors 8.1/8.2).
