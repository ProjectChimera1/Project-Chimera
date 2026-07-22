---
title: 'Story 8.2 — Godot-free ILLMProvider (Anthropic/Ollama/OpenRouter) + four-state availability UI + Test-connection'
type: 'feature'
created: '2026-07-21'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '5a98597585dc64e0c89fc7b1fadd601252e5f8e6'
final_revision: '929cf74447fc0d2aa61ebdb66081130612ac40de'
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-8-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-8-1-provider-config-isecretstore-provider-model-baseurl-in-versioned-settingsdata.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** AI authoring today runs through `LLMService`'s two hardcoded HTTP calls with an implicit Claude→Ollama fallback — the selected provider is not authoritative, there is no OpenRouter path, no host allowlist, no response-size cap, and no way for a creator to enter a key, pick a provider/model, test the connection, or understand *why* AI is unavailable. Story 8.1 landed the config plumbing (secret store, versioned settings, provider catalog); nothing consumes it yet.

**Approach:** Add a Godot-free `ILLMProvider.GenerateAsync(NormalizedRequest)→NormalizedResult` abstraction over three hand-rolled adapters (Anthropic `/v1/messages`, Ollama `/api/chat`, OpenRouter `/chat/completions`) using only `System.Net.Http` + `System.Text.Json` — no vendor SDK, AOT-clean, no silent fallback, cloud hosts on a pinned allowlist, response bytes capped. Add a Godot-free availability evaluator + Test-connection round-trip that classifies the connection into one of four unavailable states or healthy, and surface it as the four-state UI in the Settings provider-config section (with key entry + provider/model pickers) and as an availability status on the trigger/map AI panels. This story does NOT repoint `LLMService`'s generate methods onto the stack — that is Story 8.3; here the stack, the config UI, and Test-connection are built and independently proven.

## Boundaries & Constraints

**Always:**
- All new provider/evaluator code is Godot-free (no `using Godot;`), lands under `godot/src/AI/` so `SimSources.props` globs it into the Tier-1 xUnit harness AND the determinism analyzer, and depends only on `System.Net.Http`, `System.Text.Json`, `System.Threading`, and `ProjectChimera.Core.*`.
- The selected provider is authoritative: `GenerateAsync` and Test-connection use ONLY the configured provider's adapter and NEVER fall back to another provider on failure.
- Keys are read exclusively via `ISecretStore.Get(SecretIds.Llm)` — never from an `[Export]` field, `SettingsData`, or a literal.
- Cloud provider requests (anthropic, openrouter) only proceed when the resolved endpoint host is on a pinned allowlist; a base-URL override to a non-allowlisted host is rejected before any network call. Ollama is local (loopback) and permitted without a key.
- Every adapter caps the buffered response at a fixed byte ceiling; a response exceeding it fails as a malformed/oversized result rather than being read unbounded into memory.
- Adapters accept an injected `HttpClient`/`HttpMessageHandler` so every adapter, the factory, and the evaluator are unit-testable against stub endpoints with no live network.
- The four availability states are distinct and each maps to its own creator-facing message; in every state the editor remains fully usable manually (the panels and surrounding editors keep working with AI affordances disabled/explained).
- Any UI update produced by an async Test-connection/generation callback is marshalled to the Godot main thread.

**Block If:**
- Story 8.1's frozen surfaces (`LlmProviderCatalog` shape, `SettingsData.Llm*` fields + `MigrateForward`, `ISecretStore`/`SecretIds`, `SceneContext.SecretStore`) would have to change incompatibly to proceed — the consuming design must fit them as-is.

**Never:**
- Do NOT modify `LLMService.GenerateTriggerAsync`/`GenerateScenarioAsync`, its `TryClaudeAsync`/`TryOllamaAsync`, or the trigger/scenario validators — repointing generation onto `ILLMProvider` and extending prompt schemas is Story 8.3. (Removing the fallback path lives with that repoint.)
- No vendor SDK / NuGet package (Anthropic, OpenAI, LangChain, etc.). No streaming (blocking v1).
- No sim-layer coupling: none of this runs in the deterministic tick; no `Fixed`/float determinism concern (the provider layer is string-in/string-out).
- Do not persist the API key anywhere except the secret store.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Adapter dispatch | provider=anthropic/ollama/openrouter, valid stub endpoint | Correct adapter posts to the right path with the right headers/body; `NormalizedResult.Ok=true`, `Text` = extracted content | n/a |
| No fallback | selected provider's stub returns 500 / throws | `NormalizedResult.Ok=false` with that provider's failure; NO other adapter is invoked | Failure surfaced verbatim, not masked by a fallback success |
| Missing key (cloud) | provider=anthropic, secret store empty | Evaluator returns `NoKey`; no request attempted | Config-derived, synchronous |
| Missing key (local) | provider=ollama, secret store empty | Treated as configured (Ollama needs no key); Test-connection proceeds | n/a |
| Unknown provider | settings provider id not in catalog | Evaluator returns `NoProvider` | Config-derived, synchronous |
| Non-allowlisted host | provider=anthropic, base-URL override host not on allowlist | Factory refuses to build; treated as unavailable; no request | Rejected pre-flight with a located reason |
| Host reachable, healthy | configured + reachable stub returns parseable body | Test-connection → `Healthy` | n/a |
| Host unreachable | stub handler throws `HttpRequestException`/timeout | Test-connection → `Unreachable` | Distinct from `FailedValidation` |
| Returned-but-unparseable | stub returns 200 with junk/empty/невalid JSON shape | Test-connection → `FailedValidation` | Distinct from `Unreachable` |
| Oversized response | stub returns a body beyond the byte cap | `NormalizedResult` fails as malformed/oversized; buffer bounded | Not read unbounded |

</intent-contract>

## Code Map

- `godot/src/AI/Providers/ILLMProvider.cs` -- NEW. `ILLMProvider { string ProviderId; Task<NormalizedResult> GenerateAsync(NormalizedRequest, CancellationToken); }` + `NormalizedRequest` (SystemPrompt, UserMessage, MaxTokens) + `NormalizedResult` (Ok, Text, Error, `NormalizedFailure` kind ∈ None/Unreachable/HttpError/MalformedResponse) with `Success(text)`/`Fail(kind,msg)` factories.
- `godot/src/AI/Providers/AnthropicProvider.cs` -- NEW. Adapter → `{baseUrl}/v1/messages`; headers `x-api-key`, `anthropic-version: 2023-06-01`; body `{model,max_tokens,system,messages:[{role:user,content}]}`; parse `content[0].text`. Mirrors `LLMService.TryClaudeAsync` shape.
- `godot/src/AI/Providers/OllamaProvider.cs` -- NEW. Adapter → `{baseUrl}/api/chat` (per epic; NOT the legacy `/api/generate`); body `{model,messages:[{role:system},{role:user}],stream:false}`; parse `message.content`. No key.
- `godot/src/AI/Providers/OpenRouterProvider.cs` -- NEW. Adapter → `{baseUrl}/chat/completions`; header `Authorization: Bearer {key}`; body `{model,messages:[{role:system},{role:user}]}`; parse `choices[0].message.content`.
- `godot/src/AI/Providers/LlmHttp.cs` -- NEW. Shared adapter helpers: bounded response read (`MaxResponseBytes` cap → `MalformedResponse` on overflow), exception→`NormalizedFailure` mapping (network/timeout → `Unreachable`, non-2xx → `HttpError`, parse miss → `MalformedResponse`). Keeps the three adapters thin and identical in their failure taxonomy.
- `godot/src/AI/Providers/LlmHostAllowlist.cs` -- NEW. Pinned cloud hosts (`api.anthropic.com`, `openrouter.ai`) + loopback rule for ollama; `IsAllowed(providerId, Uri)`.
- `godot/src/AI/Providers/LlmProviderFactory.cs` -- NEW. `TryCreate(SettingsData, ISecretStore, HttpClient, out ILLMProvider?, out AiAvailability failure)`: resolves base URL (settings override else catalog default), model, key (secret store), enforces allowlist; returns `false`+`NoProvider`/`NoKey` for the synchronous-unavailable cases, else `true`+adapter.
- `godot/src/AI/Providers/AiAvailability.cs` -- NEW. `enum AiAvailability { Healthy, NoProvider, NoKey, Unreachable, FailedValidation }` + `AiAvailabilityMessages.Describe(state)` — distinct UX-DR52-voiced creator messages ("Commander", terse, mechanical). Godot-free so the microcopy is Tier-1 assertable.
- `godot/src/AI/Providers/AiAvailabilityEvaluator.cs` -- NEW. Ctor takes an `HttpClient`. `EvaluateConfig(SettingsData, ISecretStore) → AiAvailability` (NoProvider/NoKey or Healthy-candidate); `Task<AiAvailability> TestConnectionAsync(SettingsData, ISecretStore, CancellationToken)` → builds provider via factory, runs a minimal round-trip, maps result to Healthy/Unreachable/FailedValidation. No fallback.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- EDIT. Add `public AI.Providers.AiAvailabilityEvaluator AiEvaluator = null!;`.
- `godot/src/Core/Bootstrap/Phases/SettingsPhase.cs` -- EDIT. Construct one shared `HttpClient` (short timeout, UA) + `AiAvailabilityEvaluator`, publish `_ctx.AiEvaluator`; pass evaluator + `_ctx.SecretStore` into `SettingsPanel.Initialize` for the provider-config section.
- `godot/src/UI/SettingsPanel.cs` -- EDIT. Add an "AI Provider" section: provider dropdown (catalog), model picker + free-text override, base-URL override field, masked API-key field (writes `ISecretStore.Set(SecretIds.Llm, …)` / reads `Has`), and a **Test connection** button that runs `TestConnectionAsync`, shows a "Transmuting…" spinner during the call, and renders the resulting four-state message. Persists provider/model/baseUrl into `SettingsManager.Current` + `Save()`.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` -- EDIT. On open, drive an availability status line from `AiEvaluator.EvaluateConfig`; when unavailable show the state message and disable Generate while manual trigger authoring stays enabled. (Wire the evaluator through `TriggerEditorPhase`.)
- `godot/src/CreationSuite/MapGeneratorPanel.cs` -- EDIT. Same availability status wiring; when unavailable the panel explains the state and the surrounding editor stays usable. (Wire through `MapGeneratorPhase`.)
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs`, `MapGeneratorPhase.cs` -- EDIT. Pass `_ctx.AiEvaluator` (+ `_ctx.SecretStore`) into the two panels' `Initialize`.
- `godot/ProjectChimera.Sim.Tests/AI/` -- NEW test files (below). A `StubHttpMessageHandler` + fake `ISecretStore` are test-local helpers here.

## Tasks & Acceptance

**Execution:**
- `godot/src/AI/Providers/ILLMProvider.cs` -- Author the contract + `NormalizedRequest`/`NormalizedResult`/`NormalizedFailure` -- the provider-agnostic seam every adapter and the evaluator speak.
- `godot/src/AI/Providers/LlmHttp.cs` -- Bounded-read + failure-mapping helpers -- one byte cap + one failure taxonomy shared by all adapters.
- `godot/src/AI/Providers/AnthropicProvider.cs`, `OllamaProvider.cs`, `OpenRouterProvider.cs` -- The three adapters -- correct path/headers/body/parse per provider; no SDK.
- `godot/src/AI/Providers/LlmHostAllowlist.cs` -- Pinned-host guard -- cloud requests only to allowlisted hosts; ollama loopback allowed.
- `godot/src/AI/Providers/LlmProviderFactory.cs` -- Build provider from settings+secret+http -- single construction site; enforces key/allowlist; emits the synchronous unavailable states.
- `godot/src/AI/Providers/AiAvailability.cs` -- Four-state enum + distinct messages -- single source of the four-state microcopy.
- `godot/src/AI/Providers/AiAvailabilityEvaluator.cs` -- Config eval + Test-connection round-trip -- classifies availability with no fallback.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + `SettingsPhase.cs` -- Add + construct + publish `AiEvaluator` (shared HttpClient) -- one wiring site, first phase.
- `godot/src/UI/SettingsPanel.cs` -- Provider-config section + key entry + Test-connection + spinner + state message -- the canonical four-state UI and where a creator supplies the key.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` + `MapGeneratorPanel.cs` + `TriggerEditorPhase.cs` + `MapGeneratorPhase.cs` -- Availability status on each AI panel; manual authoring stays usable -- four-state message on every AI panel.
- `godot/ProjectChimera.Sim.Tests/AI/LlmProviderAdapterTests.cs` -- Each adapter against a `StubHttpMessageHandler`: asserts URL path, headers, request body shape, and parsed `Text`; asserts no vendor package is referenced (the test assembly compiles with only System.Net.Http).
- `godot/ProjectChimera.Sim.Tests/AI/LlmProviderFactoryTests.cs` -- Correct adapter per provider id; base-URL override vs catalog default; key sourced from secret store; non-allowlisted host → `NoProvider`-class refusal; no-fallback (a failing provider never yields another's success).
- `godot/ProjectChimera.Sim.Tests/AI/LlmHostAllowlistTests.cs` -- Allowlisted cloud hosts pass; arbitrary cloud host rejected; ollama loopback allowed.
- `godot/ProjectChimera.Sim.Tests/AI/LlmResponseCapTests.cs` -- Oversized stub body → malformed/oversized failure; buffer bounded.
- `godot/ProjectChimera.Sim.Tests/AI/AiAvailabilityEvaluatorTests.cs` -- Drives all five states via stub handler + fake secret store: NoProvider, NoKey, Healthy, Unreachable, FailedValidation.
- `godot/ProjectChimera.Sim.Tests/AI/AiAvailabilityMessagesTests.cs` -- Each state → a distinct, non-empty message.

**Acceptance Criteria:**
- Given a `NormalizedRequest` and each of the three providers selected in turn, when `GenerateAsync` runs against a stub endpoint, then the correct adapter is used (right path/headers/body) and returns a `NormalizedResult` whose `Text` is the parsed content, and only `System.Net.Http`/`System.Text.Json` are used (no vendor SDK reference).
- Given a selected provider whose endpoint fails, when `GenerateAsync` or Test-connection runs, then it reports that provider's failure and does NOT invoke any other provider's adapter.
- Given a configured, reachable provider, when Test-connection runs a minimal round-trip, then it returns `Healthy`, the buffered response is capped at the fixed ceiling, the resolved cloud host is on the pinned allowlist, and the key was read from `ISecretStore` (never an `[Export]` field or settings).
- Given, in turn, no provider configured / provider-set-but-no-key / an unreachable host / a returned-but-unparseable response, when a creator opens an AI panel or presses Test-connection, then each case shows its own distinct four-state message and the editor stays fully usable manually.
- Given a provider/model/base-URL chosen and a key entered in the Settings provider-config section, when settings are saved, then provider/model/baseUrl round-trip through `SettingsData` and the key is written only to `ISecretStore` (absent from `settings.json`).
- Given the codebase after this story, when `LLMService`'s generate methods and validators are inspected, then they are unchanged (repointing is Story 8.3).

## Design Notes

Scope boundary vs 8.3 (avoids the intent gap): 8.2's ACs test `GenerateAsync` against a stub, the no-fallback contract, Test-connection, and the four-state UI — none mention repointing trigger/map generation. So 8.2 builds the stack + config UI + Test-connection and proves them in isolation; 8.3 repoints `LLMService` onto `ILLMProvider` (removing the live Claude→Ollama fallback there) and extends prompt schemas. The stack itself has no fallback path by construction, which is what "REPLACES the implicit fallback" means at this layer.

Availability taxonomy split: `NoProvider`/`NoKey` are config-derived and computed synchronously (`EvaluateConfig`) — cheap enough to run on panel-open. `Unreachable`/`FailedValidation` require a network round-trip and are produced by `TestConnectionAsync` (and by a failed generate attempt later). "Requires a key" = provider is not `ollama` (loopback local). This keeps 8.1's `LlmProviderCatalog` frozen — the key-requirement rule lives in the 8.2 evaluator, not the catalog data.

Adapter test seam: adapters take an injected `HttpClient`; tests pass one built over a `StubHttpMessageHandler` that records the outgoing `HttpRequestMessage` (URL/headers/body) and returns a canned response — no live network, deterministic. The byte cap is enforced by reading the response via a bounded stream read, not `ReadAsStringAsync`, so an oversized body fails before full materialization.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests pass, including the new `AI/` adapter/factory/allowlist/cap/evaluator/messages tests; no prior test regresses.
- `dotnet build godot/godot.sln` -- expected: the full Godot C# project compiles with the new provider stack + panel wiring.
- `grep -rn "using Godot" godot/src/AI/Providers/` -- expected: no matches (the whole provider layer is Godot-free, so `SimSources.props`'s `src/AI/**` glob keeps it Tier-1 + analyzer-covered).
- `grep -rn "Anthropic\|OpenAI\|LangChain" godot/ProjectChimera.Sim.Tests/packages.lock.json godot/*.csproj` -- expected: no vendor-SDK package (only System.Net.Http).

**Manual checks (in-engine, Godot-coupled UI not reachable by the headless harness):**
- Open Settings → AI Provider: pick each provider, enter a key, press Test connection; confirm the spinner shows and the four states render distinctly (clear key → NoKey; bad base URL → Unreachable/rejected; healthy provider → Healthy). Confirm the key is written to `user://secrets/llm.key` and is absent from `user://settings.json`.
- Open the trigger and map AI panels with no provider/key: confirm each shows its four-state message, Generate is disabled, and manual authoring / the surrounding editor still works.

## Review Triage Log

### 2026-07-21 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 0, medium 4, low 1)
- defer: 0
- reject: 15
- addressed_findings:
  - `[medium]` `[patch]` The cloud adapters added their auth header (`x-api-key` / `Authorization`) OUTSIDE `LlmHttp.SendAsync`'s try/catch, so a pasted key with an invalid header character (embedded space/newline/control char) made `HttpHeaders.Add` throw `FormatException` straight out of `GenerateAsync` — violating the documented "never throws for a provider-side failure" contract the 8.3 generate path relies on, and (in 8.2) getting re-labelled "host unreachable" by Test-connection's broad catch. Wrapped the header add in both `AnthropicProvider`/`OpenRouterProvider` → returns `Fail(HttpError→FailedValidation)` and never dispatches; added two Tier-1 regression tests. (Blind Hunter)
  - `[medium]` `[patch]` `LlmResponseCapTests.OversizedBody_FailsAsMalformed` — the one test proving the byte cap fires THROUGH an adapter — used a fixture with no `type:"text"` block, so it failed on shape rejection, not the cap: dropping the bounded read entirely (unbounded-memory regression) left it green. Gave the oversized fixture a valid `{"type":"text",…}` block so the ONLY reason it can fail is the cap firing, making the test sensitive to the `SendAsync`→`ReadBoundedAsync` wiring. (Verification Gap)
  - `[medium]` `[patch]` `SettingsPanel.BuildAiSettingsSnapshot` (what Test-connection AND the config status line validate) did NOT run `MigrateForward`, while `ApplyAndSave` did — so the config tested/reported could diverge from the config saved, and an empty model presented as config-"ready" with Generate enabled though the factory built a provider that would fail at runtime. Normalized the snapshot with the same `MigrateForward` Apply uses, so the two paths can never disagree (empty model → default in both). (Blind Hunter + Edge Case Hunter)
  - `[medium]` `[patch]` During an in-flight Test-connection the "Clear key" button stayed enabled, so a main-thread `_secretStore.Clear()` could race the background probe's `Get()`/restore on the lock-free `FileSecretStore` (and its post-test failure-restore would silently undo the user's Clear); and `CompleteTest` bailed at `!IsInstanceValid` BEFORE restoring the prior key, so a panel freed mid-test left the unverified probe key persisted over a previously-valid one. Disabled Clear-key for the test duration (re-enabled in `CompleteTest`) and moved the secret-store restore ahead of the UI-validity guard. (Blind Hunter + Edge Case Hunter)
  - `[low]` `[patch]` The prior pass's Anthropic "first TEXT block" selection (guarding a thinking/tool_use-first response from misclassification — load-bearing for the shared 8.3 generate path) had no test; a revert to `content[0]` would ship green since every fixture had text at index 0. Added a multi-block (`thinking`-first, then `text`) adapter test asserting the text block is parsed. (Verification Gap)

### 2026-07-21 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 1, medium 3, low 1)
- defer: 1
- reject: 10
- addressed_findings:
  - `[high]` `[patch]` The shared AI `HttpClient` (`SettingsPhase`) followed redirects (`AllowAutoRedirect` default true); the host allowlist is checked only against the INITIAL base URL, and .NET does not strip the custom `x-api-key` header on a cross-host redirect — a 302 from an allowlisted host would exfiltrate the Anthropic key. Built the client with `HttpClientHandler { AllowAutoRedirect = false }`. (Blind Hunter)
  - `[medium]` `[patch]` `AiAvailabilityEvaluator.EvaluateConfig` (which the two panels gate their Generate button on) checked only catalog+key, NOT the base-URL parse / host allowlist the factory enforces — so a non-allowlisted or malformed base-URL override presented as "AI: ready" with Generate enabled though generation is impossible. Delegated `EvaluateConfig` to `LlmProviderFactory.TryCreate` so the sync state can never disagree with the real path (also unifies the key-presence predicate); added non-allowlisted + malformed-base-URL regression tests. (Blind Hunter + Verification Gap + Intent Alignment)
  - `[medium]` `[patch]` `LlmHttp.SendAsync` read the body outside its try/catch, so a mid-stream `IOException`/reset escaped `GenerateAsync`, violating the documented never-throw contract (which the 8.3 repoint will rely on); and `ResponseHeadersRead` left the body read uncovered by `HttpClient.Timeout`, so a stalled body hung Test-connection forever. Wrapped the body read (mid-stream failure → Unreachable, genuine cancel still propagates) and bounded it with a linked-CTS deadline; added mid-stream-failure / client-timeout / genuine-cancellation tests. (Edge Case Hunter + Verification Gap)
  - `[medium]` `[patch]` Test-connection persisted the typed key BEFORE the round-trip and blanked the field, so a failed test (401) durably overwrote a previously-valid key and hid the typo. Reworked to snapshot the prior key, probe with the typed key, and keep it only on Healthy — else restore the prior key and leave the field populated. (Edge Case Hunter + Blind Hunter)
  - `[low]` `[patch]` `AnthropicProvider` extracted `content[0].text`, assuming the first Messages-API block is text; a thinking/tool_use-first response (the shared 8.3 generate path) would misclassify a healthy answer. Now selects the first block whose `type == "text"`; updated two fixtures to the realistic block shape. (Blind Hunter)

## Auto Run Result

### Follow-up review pass — 2026-07-21

An independent follow-up review (the prior pass set `followup_review_recommended: true`) re-ran all four layers (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full baseline→HEAD diff. Intent Alignment found no intent gap (the four-unavailable-states-plus-Healthy taxonomy and the config-status-on-panels / round-trip-in-Settings split are coherent and honestly disclosed). No `bad_spec`, no `intent_gap`.

**5 patches applied (medium 4, low 1), all verified green:**
- `AnthropicProvider.cs` / `OpenRouterProvider.cs` — auth-header add wrapped so a malformed key returns `Fail(HttpError→FailedValidation)` instead of throwing out of `GenerateAsync` (honours the never-throws contract; +`using System;`).
- `LlmResponseCapTests.cs` — oversized-body fixture given a valid text block so the test actually exercises the byte cap (was passing on shape rejection).
- `LlmProviderAdapterTests.cs` — +3 tests: Anthropic thinking-first multi-block parse, Anthropic + OpenRouter malformed-key-never-throws.
- `SettingsPanel.cs` — `BuildAiSettingsSnapshot` now `MigrateForward`s (Test/status can't diverge from Apply; no false config-"ready" for an empty model); "Clear key" disabled during a test; `CompleteTest` restores the prior key before the UI-validity guard (freed-panel + Clear-race safe).

**Follow-up verification:** `dotnet test` → 2861 passed, 1 skipped, 0 failed (+3 over the prior pass's 2858). `dotnet build godot.sln` → 0 errors. `grep "using Godot" src/AI/Providers/` → none. No vendor SDK package. 15 findings rejected (see below); 0 newly deferred (the remote-LAN-Ollama item remains the single pre-existing deferred entry — not re-added).

**Rejected (15, why):** cancelled-in-flight-test key leak (dead path today — the Test button gates re-entry so the only cancel trigger is inert, and a naive restore would clobber a re-entrant probe); shared-`HttpClient`/`_testCts` not disposed (lifetime-scoped, standard); `RefreshAvailability` duplicated / magic status colors / `null!`-then-null-check (cosmetic); Ollama "ready" is config-only (Reading F, disclosed by the label); catalog↔factory 4th-provider incoherence (no 4th provider; safe `_ =>` fallback); oversized-before-status taxonomy nuance (both collapse to FailedValidation); 30s body-read deadline > 15s client timeout (deliberate hang-bound, cap keeps bodies small); billable Test completion (spec mandates a minimal round-trip); mid-stream-failure→Unreachable label (prior pass's documented choice); `secretStore!` null-forgiving (callers pass non-null); remote-LAN-Ollama (already deferred); redirect-defense / key-state-machine / panel-gating untested (Godot-coupled bootstrap+UI — the intent scopes these to manual verification; carried as residual risk).

**Follow-up review recommended:** false — this pass converged to localized hardening (medium/low only, no high, no intent/spec issue); the testable surfaces are now covered (+3 tests) and the remaining edits are small and mechanical. Further independent passes have diminishing returns.

**Residual risks (unchanged from the initial pass):** the Godot-coupled UI (Settings AI section, panel status/gating, the no-redirect `HttpClient` handler config, the key snapshot/restore state machine) is not unit-testable — correctness rests on compilation + the in-engine manual checks; the load-bearing Godot-free logic is fully Tier-1 covered. Single shared `SecretIds.Llm` key across providers (already-tracked `deferred-work.md` item). AOT trim advisories (IL2026/IL3050) on anonymous-type serialize, matching the existing `LLMService` convention. `*.cs.uid` residual artifacts left untracked.

---

### Initial pass

Status: done

**Summary:** Added a Godot-free `ILLMProvider` stack (Anthropic/Ollama/OpenRouter adapters over `System.Net.Http` only — no vendor SDK, no silent fallback, pinned host allowlist, 1 MiB bounded response read), an `AiAvailability` four-state evaluator + Test-connection round-trip, a Settings "AI Provider" section (provider/model/base-URL pickers, secret-store key entry, Test-connection + "Transmuting…" spinner), and availability gating on the trigger/map AI panels. `LLMService` generation is untouched — repointing it onto the stack is Story 8.3, per the epic's AC split.

**Files changed:**
- `godot/src/AI/Providers/ILLMProvider.cs` — the seam + `NormalizedRequest`/`NormalizedResult`/`NormalizedFailure`.
- `godot/src/AI/Providers/LlmHttp.cs` — shared bounded-read (deadline-guarded, never-throw) + failure taxonomy + content-parse chokepoint.
- `godot/src/AI/Providers/{Anthropic,Ollama,OpenRouter}Provider.cs` — the three no-SDK adapters.
- `godot/src/AI/Providers/LlmHostAllowlist.cs` — pinned cloud hosts + ollama loopback rule.
- `godot/src/AI/Providers/LlmProviderFactory.cs` — single construction site; enforces key + URL + allowlist; no fallback.
- `godot/src/AI/Providers/AiAvailability.cs` — five-state enum + distinct "Commander" microcopy.
- `godot/src/AI/Providers/AiAvailabilityEvaluator.cs` — config eval (delegates to the factory) + Test-connection round-trip.
- `godot/src/Core/Bootstrap/Phases/{SceneContext,SettingsPhase,TriggerEditorPhase,MapGeneratorPhase}.cs` — wire a shared no-redirect `HttpClient` + evaluator into the panels.
- `godot/src/UI/SettingsPanel.cs` — AI Provider tab + Test-connection (key snapshot/restore).
- `godot/src/CreationSuite/{TriggerEditorPanel,MapGeneratorPanel}.cs` — four-state status + Generate gating.
- `godot/ProjectChimera.Sim.Tests/AI/*` — 51 Tier-1 tests (adapters, factory, allowlist, cap, evaluator, messages, HTTP resilience) + `StubHttpMessageHandler`/`FakeSecretStore`.

**Review findings:** 5 patches applied (high 1, medium 3, low 1) — see Review Triage Log. 1 deferred (LAN-hosted Ollama, in `deferred-work.md`). 10 rejected (fallback-repoint is Story 8.3; Test button serializes concurrency; UTF-8 decode / key-trim / live-refresh are benign). 0 intent_gap, 0 bad_spec.

**Verification:** `dotnet test` full suite → 2858 passed, 1 skipped, 0 failed (+5 over baseline). `dotnet build godot.sln` → 0 errors. `grep "using Godot" src/AI/Providers/` → none (Godot-free, Tier-1 + analyzer covered). No vendor SDK in packages.lock/csproj. In-engine manual checks (Settings AI-Provider four-state walk-through; panel status/gating) left for manual verification — Godot-coupled UI is outside the headless harness (mirrors 8.1's boundary).

**Follow-up review recommended:** true — the review pass made a high-severity security fix (redirect key-leak) plus three medium correctness/contract fixes spanning the HTTP transport, the availability state machine, and secret handling, across Godot-free and Godot-coupled surfaces; an independent pass would be valuable.

**Residual risks:**
- The Godot-coupled UI (Settings AI section, panel status wiring) is not unit-testable; correctness rests on compilation + the in-engine manual checks. The load-bearing logic (adapters/factory/allowlist/cap/evaluator/four-state) is fully Tier-1 covered.
- Single shared `SecretIds.Llm` key across all providers — an already-tracked `deferred-work.md` item from the 8.1 review (per-provider key storage); unchanged here.
- Adapters emit AOT trim advisories (IL2026/IL3050) on `JsonSerializer.Serialize` of anonymous types — identical to the existing `LLMService` convention; analyzer gate passes with 0 errors.
- Residual artifacts left untracked (not part of this change): `*.cs.uid` for the new test files and pre-existing 8.1 `.uid` files.
