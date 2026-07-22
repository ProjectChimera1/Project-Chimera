---
title: 'Story 8.1 — Provider config: ISecretStore + provider/model/baseUrl in versioned SettingsData'
type: 'feature'
created: '2026-07-21'
status: 'done'
baseline_revision: 'b7c1d5131d22cb2b11816bc242d9776d80b0b29c'
final_revision: '820376b8ae3a26aeb2595a5fefe9b2b00265c486'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-8-context.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** LLM/mod.io API keys live in plaintext `[Export]` string fields on `MainScene` (`AnthropicApiKey`, `ModIoApiKey`) — hardcodable, committable, and shippable inside a build — and `SettingsData` has no provider/model/baseUrl fields and no schema version, so an AI provider choice can neither persist nor migrate forward.

**Approach:** Add a Godot-free `ISecretStore` with a file-backed impl over a gitignored `user://secrets/*.key`; rip the two plaintext key fields off `MainScene` and re-source `LLMService`/`ModIoService` keys from the store; add a schema-version plus `llm_provider`/`llm_model`/`llm_base_url` fields to `SettingsData` with forward-migrating safe defaults and a curated, code-backed provider/model catalog. Provider *consumption* (the ILLMProvider abstraction, removing the Claude→Ollama fallback) is Story 8.2 — this story is config plumbing only.

## Boundaries & Constraints

**Always:**
- `ISecretStore`, `FileSecretStore`, `SecretMigration`, the provider catalog, and all `SettingsData` changes are **Godot-free** (no `using Godot;`) — they land under `src/Core/**` which `SimSources.props` globs into both the Tier-1 test harness and the AOT/banned-API analyzer gate. Use only `System.IO`/`System.Text.Json` (mirror `LocalProfileSource`).
- The store takes an OS-absolute directory injected by the Godot layer; the Godot layer resolves `user://secrets` via `ProjectSettings.GlobalizePath`. Secrets live only under `user://` — never `res://` (Godot packs only `res://` into the PCK).
- `Get` on an absent secret returns `""`, never throws, and writes **nothing** to disk. The directory/file is created lazily only on `Set`.
- Fail-soft on read (missing dir/file or unparseable → `""`), mirroring `SettingsManager.Load`.
- `SettingsData` stays backward-compatible: an older `settings.json` lacking the new fields loads to safe defaults with no error; default model is `claude-sonnet-4-6`; the API key is **never** written into `settings.json`.
- Preserve the pinned bootstrap phase order — do NOT add a new phase (that touches `ScenePhaseOrder`/`PhaseOrderTest`/`ScenePhaseRunner`). Construct the store inside the existing `SettingsPhase`.

**Block If:**
- (none anticipated) The two merged halves (secret store + versioned settings) are one dependency-coupled deliverable; no independent human decision is required to proceed.

**Never:**
- Do NOT build the ILLMProvider abstraction, add adapters, remove the implicit Claude→Ollama fallback, or re-point `LLMService`'s provider logic onto the new settings fields — that is Story 8.2. This story only changes the *key source* and *adds/persists* the settings fields + catalog.
- Do NOT touch any sim-tick / determinism-hashed code. Zero sim coupling (AR-33).
- Do NOT remove `ModIoGameId` (a non-secret public game id) — only the two string key fields go.
- Do NOT commit any real key value; tests use synthetic sentinel strings.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Fresh read | no `secrets/` dir | `Get("llm")` → `""`; dir not created; nothing written | No throw |
| Set then read | `Set("llm","sk-X")` | `Get("llm")` → `"sk-X"`; file `secrets/llm.key` written | No throw |
| Restart | new `FileSecretStore` over same dir after a prior `Set` | `Get` returns the persisted value | No throw |
| Corrupt/unreadable file | `secrets/llm.key` unreadable | `Get` → `""` | Swallowed, fail-soft |
| Invalid keyId | `Get`/`Set` with `"../evil"` or empty | reject (path-traversal guard) | Throws `ArgumentException` |
| Old settings file | `settings.json` with no provider fields | provider=`anthropic`, model=`claude-sonnet-4-6`, schema version stamped, no error | Defaults applied |
| Free-text model | `LlmModel = "some-custom:tag"` | value persists and round-trips unchanged | No error |
| Migration | store empty for `llm`, legacy plaintext supplied | value copied into store, returns `true` | No-op (`false`) if store already set or legacy empty |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ISecretStore.cs` -- NEW. Godot-free interface: `string Get(string id)`, `void Set(string id, string value)`, `bool Has(string id)`, `void Clear(string id)`.
- `godot/src/Core/Definitions/FileSecretStore.cs` -- NEW. File-backed impl over an injected absolute dir; one `<id>.key` file per secret. Mirror `LocalProfileSource` (System.IO + fail-soft). keyId validated `^[a-z0-9_-]+$`.
- `godot/src/Core/Definitions/SecretMigration.cs` -- NEW. Godot-free static `MigrateLegacyKey(ISecretStore, string id, string? legacyPlaintext) -> bool` (copies only when store lacks `id` and legacy is non-empty).
- `godot/src/Core/Definitions/LlmProviderCatalog.cs` -- NEW. Godot-free curated catalog: `anthropic`/`ollama`/`openrouter`, each with display name, default base URL, and curated model list; helpers `TryGet(id)`, `Providers`, `DefaultModel` (=`claude-sonnet-4-6`).
- `godot/src/Core/Definitions/SettingsData.cs` -- EDIT. Add `schema_version` (int), `llm_provider`, `llm_model` (default `claude-sonnet-4-6`), `llm_base_url` fields + a static/instance `MigrateForward()` that normalizes unknown provider→default, empty model→default, stamps current version.
- `godot/src/UI/SettingsManager.cs` -- EDIT. Call `MigrateForward()` in `Load` after deserialize so old files migrate on load.
- `godot/src/Core/MainScene.cs` -- EDIT. Delete `[Export] string AnthropicApiKey` (~192) and `[Export] string ModIoApiKey` (~186). Keep `ModIoGameId`.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- EDIT. Add `public ISecretStore SecretStore = null!;`.
- `godot/src/Core/Bootstrap/Phases/SettingsPhase.cs` -- EDIT. Construct `new FileSecretStore(GlobalizePath("user://secrets"))`, run `SecretMigration.MigrateLegacyKey` for the LLM key (legacy value `""` in this repo), assign `_ctx.SecretStore`.
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` -- EDIT (~23,62). Source the key from `_ctx.SecretStore.Get("llm")` instead of `_ctx.Scene.AnthropicApiKey`.
- `godot/src/Core/Bootstrap/Phases/ContentBrowserPhase.cs` -- EDIT (~27-30). Gate on `_ctx.SecretStore.Has("modio")` and construct `new ModIoService(gameId, _ctx.SecretStore.Get("modio"))`.
- `.gitignore` + `godot/.gitignore` -- EDIT. Add `*.key` and `secrets/`.
- `godot/ProjectChimera.Sim.Tests/Definitions/` -- NEW test files (below).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/ISecretStore.cs` -- Author the interface -- Godot-free seam consumable by both services and testable headlessly.
- `godot/src/Core/Definitions/FileSecretStore.cs` -- Implement file-backed store -- lazy-create, fail-soft, keyId guard; per the I/O matrix.
- `godot/src/Core/Definitions/SecretMigration.cs` -- Implement one-time migration helper -- move a legacy plaintext key into the store on first run.
- `godot/src/Core/Definitions/LlmProviderCatalog.cs` -- Author curated provider/model catalog -- single source of truth for provider ids, default base URLs, curated models, default model.
- `godot/src/Core/Definitions/SettingsData.cs` -- Add schema_version + provider/model/baseUrl + MigrateForward -- versioned, forward-migrating settings.
- `godot/src/UI/SettingsManager.cs` -- Call MigrateForward in Load -- old files normalize on load.
- `godot/src/Core/MainScene.cs` -- Remove the two `[Export]` key fields -- no plaintext key surface.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + `SettingsPhase.cs` -- Add + populate `SecretStore` -- shared, constructed once, first phase.
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` + `ContentBrowserPhase.cs` -- Re-source keys from `SecretStore` -- services obtain keys from `ISecretStore`.
- `.gitignore`, `godot/.gitignore` -- Ignore `*.key` + `secrets/` -- keys never committed.
- `godot/ProjectChimera.Sim.Tests/Definitions/FileSecretStoreTests.cs` -- Cover every I/O-matrix row -- fresh read, round-trip, restart, corrupt, invalid keyId, no-write-until-Set.
- `godot/ProjectChimera.Sim.Tests/Definitions/SecretMigrationTests.cs` -- Cover migrate/no-op branches.
- `godot/ProjectChimera.Sim.Tests/Definitions/SettingsProviderConfigTests.cs` -- Round-trip new fields; old-file-defaults; free-text override; provider→curated-model list; schema version stamped.
- `godot/ProjectChimera.Sim.Tests/Definitions/LlmProviderCatalogTests.cs` -- Three providers present; default model `claude-sonnet-4-6`; each has base URL + ≥1 model; lookup + unknown-id.
- `godot/ProjectChimera.Sim.Tests/Definitions/SecretExclusionTest.cs` -- Assert secret paths are `user://secrets`-rooted (never `res://`, structurally unpackable) AND scan the committed `godot/scenes/*.tscn` + `godot/src/**/*.cs` tree for any `[Export]` key field named `AnthropicApiKey`/`ModIoApiKey` or a key-shaped plaintext literal → none; fail loudly if found.

**Acceptance Criteria:**
- Given a fresh project with no secret file, when a provider key is read, then `ISecretStore.Get` returns `""`, no exception is thrown, and nothing is written to disk until a key is explicitly saved.
- Given a key saved via `ISecretStore.Set`, when a new store instance reads it back (restart), then the value is returned from `user://secrets/llm.key`, and `*.key`/`secrets/` are gitignored.
- Given the codebase after this story, when `MainScene` and all `[Export]` members are grepped, then no `[Export]` string field named `AnthropicApiKey` or `ModIoApiKey` exists, and `LLMService`/`ModIoService` obtain their key from `ISecretStore` at their wiring site.
- Given `SecretExclusionTest`, when it scans the packaged (`res://`) source/scene tree, then no stored key string and no plaintext key `[Export]` field is found, and the test fails loudly if one is introduced.
- Given an existing `settings.json` with no provider fields, when `SettingsManager` loads it, then provider defaults to a curated provider, model defaults to `claude-sonnet-4-6`, a schema-version field is present and migrated forward, and no error is raised.
- Given a selected provider + curated model saved, when settings round-trip through the `SettingsManager` serializer shape, then the same provider/model/baseUrl are restored and the key is never present in `settings.json`.
- Given a provider with a curated model list, when a model name not in the list is set as the free-text override, then that value persists and round-trips, and the catalog still exposes that provider's curated model list.

## Spec Change Log

_No bad_spec loopback occurred — the review produced patches and defers only, so the spec required no amendment._

## Review Triage Log

### 2026-07-21 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 4, low 2)
- defer: 2
- reject: 15
- addressed_findings:
  - `[medium]` `[patch]` Bare `"llm"`/`"modio"` id literals duplicated across 4 Godot-coupled (unit-untestable) wiring sites — extracted `SecretIds` (Godot-free), re-pointed SettingsPhase/TriggerEditorPhase/ContentBrowserPhase, added a Tier-1 constant + valid-key-id test.
  - `[medium]` `[patch]` `SecretMigration.MigrateLegacyKey` could propagate a `Set` disk-failure/invalid-id throw and abort bootstrap — wrapped the write in try/catch → best-effort (returns false); explicit user saves still surface failures.
  - `[medium]` `[patch]` `SettingsManager.Load → MigrateForward` load-time normalization was untested (Node is Godot-coupled) — extracted `SettingsData.FromJson` deserialize+migrate seam, routed Load through it, added Tier-1 tests pinning unknown-provider/empty-model normalization + null fallback.
  - `[medium]` `[patch]` `SecretExclusionTest` guard missed the idiomatic multi-line `[Export]` form, non-`ApiKey` secret field names, and scanned only `src/`+`scenes/` (a subset of the packed `res://` tree) — made detection multi-line + name-broadened (ApiKey/Secret/Token, not bare "Key" so the legit NakamaKey is not flagged), extended the literal scan to `resources/` text, and added guard self-tests proving it fires on reintroductions and not on benign fields.
  - `[low]` `[patch]` `MigrateForward` did not null-normalize `LlmBaseUrl` (explicit JSON `null` → null propagates to 8.2) — added `LlmBaseUrl ??= ""` + a test.
  - `[low]` `[patch]` Stale doc `ContentBrowserPanel.cs:17` still cited the removed `ModIoApiKey` Inspector export — updated to reference the secret store.

### 2026-07-21 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 0, low 4)
- defer: 0
- reject: 18
- addressed_findings:
  - `[low]` `[patch]` `FileSecretStore.KeyIdPattern` used `^[a-z0-9_-]+$`; in .NET `$` matches before a trailing `\n`, so `"llm\n"` passed the path-traversal/id guard and would map to a stray `llm\n.key` file — switched to `\A…\z` (absolute anchors) and added an `InvalidKeyId` `[InlineData("llm\n")]` regression case. (Edge Case Hunter)
  - `[low]` `[patch]` `SecretExclusionTest.SecretStore_IsUserRooted_NeverResRooted` asserted `Contains("user://secrets", <SettingsPhase source>)`, satisfiable by the file's own doc comment — a `GlobalizePath("user://secret")` typo in the real call would still ship green (the "silently-ignored key" regression class). Hardened: strip full-line `//` comments (line-based, so the `//` inside `user://` is not mangled), then require the executable `GlobalizePath("user://secrets")` call. (Verification Gap — test half; the behavioral in-engine smoke remains covered by the existing ledger entry.)
  - `[low]` `[patch]` `SecretMigration`'s doc contract names both the `AnthropicApiKey` and `ModIoApiKey` legacy fields, but `SettingsPhase` ran `MigrateLegacyKey` only for the LLM key — added the symmetric `MigrateLegacyKey(secretStore, SecretIds.ModIo, "")` call so the seam matches its stated contract (still a no-op today; both legacy values are `""` in this repo). (Edge Case Hunter + Blind Hunter + Verification Gap)
  - `[low]` `[patch]` `SettingsData.FromJson` was promoted to a reusable Tier-1 seam but throws `JsonException` on malformed input while its doc only promised "Never returns null" — a future direct caller (8.2 UI writing user-editable config) could crash. Clarified the doc that it throws on malformed JSON and that fail-soft callers must catch (as `SettingsManager.Load` does); left the throw in place so the Godot caller can still log the error. (Edge Case Hunter + Blind Hunter)

## Design Notes

Mirror `LocalProfileSource` exactly: Godot-free class over an injected absolute dir; the Godot layer (`SettingsPhase`) does the single `GlobalizePath("user://secrets")` call. Each secret is its own file (`llm.key`, `modio.key`) so `AC: user://secrets/llm.key` is literal. `Set` trims and writes UTF-8 (no `WriteIndented` — the file is a bare secret, not JSON).

`SettingsData` migration is intentionally lightweight: because absent JSON fields deserialize to the C# property initializers, an old file already lands on the new defaults; `MigrateForward()` additionally resets an *unknown* provider id to the default and an empty model to `claude-sonnet-4-6`, then stamps `SchemaVersion` so a subsequent `Save` persists the version. One persisted model field (`LlmModel`) holds either a curated pick or a free-text override — no separate override field is needed; the curated list is catalog data consumed by 8.2's UI.

**Known surface boundary for the exclusion test (not an intent gap):** a live PCK export scan is outside the deterministic Godot-free xUnit harness. The enforced, deterministic surface is the strongest available structural guarantee — secrets are `user://`-rooted (Godot packs only `res://`, so a key is structurally unpackable) — plus a scan of the committed `res://` source/scene tree for plaintext key fields/literals. This observes the same intent (no key ships in a build) at the outermost surface the headless harness can reach.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests pass, including the new secret-store/settings/catalog/exclusion tests; no prior test regresses.
- `dotnet build godot/godot.sln` -- expected: the full Godot C# project compiles with the `[Export]` fields removed and the phases re-sourced.
- `grep -rn "AnthropicApiKey\|ModIoApiKey" godot/src/Core/MainScene.cs` -- expected: no matches (both `[Export]` fields removed).
- `git check-ignore -q secrets/test.key && echo ignored` -- expected: `ignored` (gitignore covers `*.key`/`secrets/`).

## Auto Run Result

Status: done

**Summary:** Landed Story 8.1 — config plumbing for AI-provider setup. Net-new Godot-free `ISecretStore` (`FileSecretStore` over a gitignored `user://secrets/<id>.key`), ripped the two plaintext `[Export]` key fields (`AnthropicApiKey`/`ModIoApiKey`) off `MainScene`, re-sourced the LLM/mod.io keys from the store in the bootstrap phases, added a curated provider/model catalog + versioned provider/model/baseUrl fields with forward migration to `SettingsData`, and added the `SecretExclusionTest` structural guard. Provider *consumption* (the `ILLMProvider` abstraction + fallback removal) is intentionally deferred to Story 8.2.

**Files changed (one line each):**
- `godot/src/Core/Definitions/ISecretStore.cs` (new) — Godot-free secret-store seam (`Get/Set/Has/Clear`).
- `godot/src/Core/Definitions/FileSecretStore.cs` (new) — file-backed impl; lazy-create, fail-soft reads, `^[a-z0-9_-]+$` key-id guard.
- `godot/src/Core/Definitions/SecretMigration.cs` (new) — best-effort legacy-key migration (fail-soft write).
- `godot/src/Core/Definitions/LlmProviderCatalog.cs` (new) — curated anthropic/ollama/openrouter catalog; default model `claude-sonnet-4-6`.
- `godot/src/Core/Definitions/SecretIds.cs` (new, patch) — canonical `llm`/`modio` ids shared by the wiring sites.
- `godot/src/Core/Definitions/SettingsData.cs` — added `schema_version` + `llm_provider`/`llm_model`/`llm_base_url` + `MigrateForward()` + `FromJson()` load seam.
- `godot/src/UI/SettingsManager.cs` — `Load` routes the present-file path through `SettingsData.FromJson` (deserialize + migrate).
- `godot/src/Core/MainScene.cs` — removed the two `[Export]` key fields (kept non-secret `ModIoGameId`).
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — added `ISecretStore SecretStore`.
- `godot/src/Core/Bootstrap/Phases/SettingsPhase.cs` — constructs the store over `user://secrets` + runs migration.
- `godot/src/Core/Bootstrap/Phases/TriggerEditorPhase.cs` — LLM key from `SecretStore.Get(SecretIds.Llm)`.
- `godot/src/Core/Bootstrap/Phases/ContentBrowserPhase.cs` — mod.io key from `SecretStore.Get(SecretIds.ModIo)`.
- `godot/src/UI/ContentBrowserPanel.cs` (patch) — doc comment updated off the removed Inspector export.
- `.gitignore`, `godot/.gitignore` — ignore `*.key` + `secrets/`.
- `godot/ProjectChimera.Sim.Tests/Definitions/{FileSecretStore,SecretMigration,SettingsProviderConfig,LlmProviderCatalog}Tests.cs` + `SecretExclusionTest.cs` (new) — Tier-1 coverage incl. the I/O-matrix rows and guard self-tests.

**Review findings breakdown:** 6 patches applied (4 medium, 2 low — id constants, migration fail-soft, load-seam testability, exclusion-guard hardening, null base-url normalization, stale doc); 2 items deferred (per-provider key storage for 8.2; in-engine wiring smoke); 15 rejected (out-of-scope-per-intent: key-entry UI is Story 8.2, plaintext-vs-encryption; or spec'd/AC-required/speculative/negligible). 0 intent gaps, 0 bad-spec loopbacks.

**Verification:**
- `dotnet test godot/ProjectChimera.Sim.Tests/…` → **2806 passed, 1 skipped, 0 failed** (+17 new tests; no prior test regressed).
- `dotnet build godot/godot.sln` → **0 errors** (11 pre-existing nullable warnings, unrelated).
- `dotnet build godot/ProjectChimera.Sim.Analysis/…` (AOT/banned-API gate) → **0 errors**; no CHM diagnostic on any new file.
- `grep AnthropicApiKey|ModIoApiKey godot/src/Core/MainScene.cs` → no matches. `git check-ignore secrets/test.key` → ignored.

**Residual risks:**
- The Godot-coupled phase→service key delivery is unit-untestable (phases excluded from the Tier-1 harness); the highest-risk part (id typos) is now pinned by `SecretIds` + a constant test, but an in-engine smoke is deferred (see ledger). Phase-order invariant (`SettingsPhase` at position 0 → consumers later) is enforced by the pinned `ScenePhaseOrder`/`PhaseOrderTest`, not by a null-guard.
- No user-facing key-entry UI exists until Story 8.2 (by epic sequencing); during the 8.1→8.2 window a key must be placed at `user://secrets/llm.key` manually. AI features degrade to the existing Ollama fallback / disabled mod.io exactly as before.
- `followup_review_recommended: true` — the patch set touched the security-relevant exclusion guard and the settings-load wiring; an independent look is worthwhile.

---

### Follow-up review pass (2026-07-21)

A second independent review pass ran four parallel layers (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the committed diff (`b7c1d51..3589335`). Result: **0 intent gaps, 0 bad-spec loopbacks, 4 low patches applied, 0 new defers, 18 rejects.**

**Patches applied this pass (all low, localized, no production behavior change):**
- `FileSecretStore.cs` — key-id regex `^…$` → `\A…\z` (a trailing-newline id like `"llm\n"` was wrongly accepted by .NET `$`); `FileSecretStoreTests.cs` — added the `"llm\n"` rejection case.
- `SecretExclusionTest.cs` — hardened `SecretStore_IsUserRooted_NeverResRooted` to bind to the executable `GlobalizePath("user://secrets")` call (comment-stripped), so a folder-name typo can no longer pass on the strength of the doc comment; added a `StripLineComments` helper.
- `SettingsPhase.cs` — added the symmetric `MigrateLegacyKey(..., SecretIds.ModIo, "")` call so the migration wiring matches `SecretMigration`'s stated two-field contract (still a no-op today).
- `SettingsData.cs` — clarified `FromJson`'s doc that it throws `JsonException` on malformed input and fail-soft callers must catch (the throw is retained so `SettingsManager.Load` can log).

**Notable rejects (on the authority of the intent):** provider *consumption* / feature-inertness (Story 8.2 by intent); at-rest key encryption + file-permission hardening (threat model is PCK-packing, not local disclosure — prior review already rejected plaintext-vs-encryption); `schema_version` downgrade-safety guard (intent asks only for *forward* migration; downgrade handling is future scope); fail-soft `Get` swallowing read errors silently (spec I/O-matrix mandates it, and the store is Godot-free so cannot log); `.gitignore` breadth (AC-required belt-and-suspenders). The two genuinely deferrable items surfaced (per-provider key storage for 8.2; in-engine phase→service wiring smoke) already have ledger entries from the first pass — **no new ledger entries were appended.**

**Verification (this pass):**
- `dotnet test godot/ProjectChimera.Sim.Tests/…` → **2807 passed, 1 skipped, 0 failed** (+1 from the new key-id case; no prior test regressed; the hardened exclusion test passes, confirming the real `GlobalizePath("user://secrets")` call is present after comment-stripping).
- `dotnet build godot/godot.sln` → **0 errors** (11 pre-existing warnings).
- AOT/banned-API analyzer gate → exit 0; no CHM diagnostic on any touched file (all CHM warnings are on pre-existing sim files).

**Follow-up recommendation:** `followup_review_recommended: false` — this pass made only four low-severity, localized fixes (a regex anchor, a test-assertion hardening, a no-op migration-symmetry call, and a doc clarification) with no production behavior change; a further independent pass is not warranted.

**Residual artifacts (not part of this change, left in place):** `sprint-status.yaml` (orchestration metadata) and untracked `*.uid` files (Godot import sidecars) remain in the working tree.
