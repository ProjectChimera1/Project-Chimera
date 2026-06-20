# D6 Briefing - LLM Provider Abstraction

> Status: RECOMMEND-AND-CONFIRM. Every decision below is a recommendation for you to confirm or override. Several sub-decisions were revised by the adversarial review (flagged inline). The headline change: this is no longer "a clean authoring-layer refactor with zero determinism impact" - the review proved AI-generated scenarios feed a **stale, raw-byte cross-peer hash** that will silently desync multiplayer, so the validation/quantization/hash coupling is now a **binding prerequisite**, not a soft hand-off.

---

## 1. Decision (recommended)

**Generalize the Claude-hardcoded `LLMService` into a Godot-free, multi-provider `ILLMProvider` abstraction in `src/AI`, move provider+model+key config into the SETTINGS system, store the raw key in a gitignored `user://` file behind a 1-method `ISecretStore` seam, and route generated content through the SAME canonical deserialize -> quantize -> validate -> hash pipeline as hand-authored content - splitting the work by milestone so the content-validation half is sequenced after D3's gate exists.**

Concretely:

- **`ILLMProvider`** with a normalized request/response, plus three thin adapters: `AnthropicProvider` (verbatim lift of `TryClaudeAsync`, LLMService.cs:164-207), `OllamaProvider` (migrated from legacy `/api/generate` concat at :211-244 to `/api/chat` role messages), and a **new** `OpenRouterProvider` (OpenAI-compatible `/chat/completions`, Bearer auth, `choices[0].message.content`).
- **`LLMService` becomes a thin orchestrator**: the duplicated inline try-Claude-then-Ollama chains (:118-131 and :455-466, byte-identical) collapse to "pick provider from `ProviderConfig`, call it, route raw text into the shared ingest pipeline."
- **`SettingsData`** gains `AiProvider`/`AiModel`/`AiBaseUrl` + a boolean "key present" handle; the **raw key never lands in settings.json** - it lives in a separate `user://` file behind `ISecretStore`.
- **`SettingsPanel`** gets an "AI" section with a provider dropdown, a per-provider model dropdown (Claude defaults to the Opus 4.8 / Sonnet 4.6 / Haiku 4.5 trio), a masked secret field, and a Test-connection button - requiring new `AddDropdownRow`/`AddSecretRow` helpers (none exist today).
- **The `[Export] AnthropicApiKey` path is ripped out** (MainScene.cs:206/:1858/:1917) and the key is re-sourced from `SettingsManager` (a Node) injected DOWN into the pure-C# provider, never the reverse (NFR-4).

This is **Option A, revised**: the original "DPAPI-now" key storage is demoted to a **plaintext-floor behind the seam** (the encryption was gold-plating a non-load-bearing surface), and the determinism/validation coupling is escalated from a hand-off to a hard prerequisite.

---

## 2. Why Option A, not B or C

### Option A - Hand-rolled `ILLMProvider` + adapters (RECOMMENDED)

The only option that (a) actually satisfies FR-29's three-provider requirement with a clean home for OpenRouter, (b) creates the single ingest seam the binding D3/D2 coupling needs, and (c) stays faithful to the codebase's own no-SDK, raw-`HttpClient`, `ConcurrentQueue`/`DrainEvents` idiom (LLMService.cs:53,:77) - zero new NuGet deps, no AOT-analyzer friction against the D3 CI gate, trivially Godot-free per NFR-4.

It kills the one *real, verified* defect (the duplicated inline fallback chain, :118-131 == :455-466) and satisfies FR-29's explicit migrate-key-to-settings text. The adapter bodies are literal lifts of working code, so brownfield fit is excellent; the only invasive edit is deleting the `[Export]` path.

**Cost:** the most code of the three (interface + 3 adapters + secret store + 2 UI helpers + settings round-trip + wiring rip-out), but each piece is small and bounded. Fits M2/M4.

### Option B - OpenAI-compatible-only + Anthropic shim (REJECTED)

The grounding and the as-built code confirm the Anthropic wire shape is **structurally different** from OpenAI's: top-level `system` string + user-only `messages` + `x-api-key`/`anthropic-version` headers + `content[0].text` (LLMService.cs:169-199) vs Bearer + `messages[]` with a system role + `choices[0].message.content`. So "it's all OpenAI" forces writing **new, lossy translation code to replace the Anthropic path that already works**, permanently couples the AI layer to OpenAI's evolving schema, and reworks Ollama onto its less-battle-tested OpenAI-compat endpoint instead of native `/api/chat`. That is *worse* for a Claude-default product (Opus 4.8 is the flagship) for negligible savings, since A's Anthropic adapter is a verbatim lift, not new work.

### Option C - Dropdown + plaintext key inside settings.json (REJECTED, but partly right)

Meets the *letter* of FR-29 in an afternoon, but plaintext-in-settings.json reproduces exactly the synced-data leak being fixed the moment settings are ever exported, offers no clean home for OpenRouter (so it doesn't actually meet the three-provider requirement without an interface anyway), and leaves the duplicated fallback chain and the unwired D3 gate in place. It would be rewritten into A later - negative net effort against the maximal-now posture D1/D2/D3 set.

**However** the adversarial SCOPE lens is correct that C's *only* real error was plaintext **inside settings.json** (a syncable artifact), not plaintext per se. The revised Option A storage decision (D6-3) adopts C's cheapness - plaintext key - but in a **separate gitignored `user://` file behind an `ISecretStore` seam**: cheaper than A's original DPAPI, safer than C's in-settings plaintext, upgrade-ready.

---

## 3. Settled / recommended sub-decisions

| # | Decision | Recommendation | Depends on a user call? |
|---|----------|----------------|-------------------------|
| D6-1 | Interface shape | Hand-rolled `ILLMProvider`, IChatClient-shaped in spirit, no SDK dep | No - aligns with house style |
| D6-2 | Streaming | Blocking v1, additive stream seam; raise timeout first | Soft - depends on D6-9 latency check |
| D6-3 | Key storage scope | **REVISED**: plaintext-floor behind `ISecretStore` seam; DPAPI/libsecret later | **YES - this changed from the original DPAPI-now rec** |
| D6-4 | Key placement | Separate `user://` file + tested exclusion invariant; never in settings.json | No |
| D6-5 | Fallback policy | Selected provider authoritative; discrete follow-up commit; default toggle ON until D6-8 ships | Mild - default-ON-vs-OFF timing |
| D6-6 | Model lists | Curated data-driven JSON + free-text override; host-pinned entries | No |
| D6-7 | Validation/quantize/hash gate | Canonical deserialize -> quantize -> shared validator -> canonical-model hash; **two** swap points (target type + validator); prefer sequencing after D3 | **YES - sequencing call** |
| D6-8 | FR-34 states | Four states + Test-connection; baseline corrected (error string, not dead null branch) | No |
| D6-9 | **NEW** Commit/hash ordering vs lockstep Ready | Generated scenario immutable + canonical-hashed before Ready; in-memory path must exchange the canonical hash | **YES - binding MP-correctness** |
| D6-10 | **NEW** Endpoint + config trust | Pin cloud hosts; loopback-only custom URL for Ollama; validate config on load; cap response size | No - security floor |

Full rationale for each is in the structured `subDecisionsToConfirm` block accompanying this briefing.

**Materially user-dependent:** D6-3 (storage scope - I reversed my own prior DPAPI-now recommendation; confirm you accept plaintext-floor-now), D6-7/D6-9 (sequencing of the content path relative to D3, and the MP-hash reconciliation - this is the load-bearing call).

---

## 4. Decisions to confirm (interactive)

1. **Storage scope (D6-3):** Accept the **revised** plaintext-key-in-a-gitignored-`user://`-file floor behind the `ISecretStore` seam for v1 (DPAPI/libsecret deferred)? Or insist on DPAPI-now despite it being Windows-only and not determinism/spec-load-bearing?
2. **Content-path sequencing (D6-7):** Sequence D6's validation/quantization/hash wiring **after** D3's `ContentLoader.Validate(model)` + canonical options exist (no interim non-canonical gate), shipping only the provider-abstraction + key-migration + UI half first? Or ship the interim gate now, gated by a golden-checksum equivalence harness?
3. **MP hash reconciliation (D6-9, binding):** Confirm the cross-peer agreement hash moves off `ComputeFileHash` (raw bytes) onto the canonical Fixed-quantized model hash, and that in-memory/AI scenarios exchange THAT hash. Without this, AI-authored MP maps desync at tick 0. (This is arguably D3's deliverable, but D6 is the trigger that exposes it - confirm ownership.)
4. **OpenRouter v1-or-fast-follow:** Is OpenRouter hard-required for the 1.0 FR-29 checkbox, or deferrable? It is the only genuinely new adapter, the least load-bearing provider (Claude is default, Ollama is the offline floor), and carries the highest schema-invalid-JSON validation-failure surface. If deferrable, v1 D6 = abstraction + key migration + UI + two proven adapters.
5. **Fallback default timing (D6-5):** Default the local-fallback toggle ON until the four-state FR-34 UI (D6-8) ships, then OFF? (Defaulting OFF before D6-8 is an FR-34 "stays usable" regression for a no-key creator.)
6. **Latency vs timeout (D6-9/D6-2):** OK to measure a representative Opus 4.8 7-pass map-gen latency and raise `TIMEOUT_MS` (LLMService.cs:69, currently 30s) before locking blocking-v1?

---

## 5. Migration sequence (strangler, golden-checksum-gated, milestone-tagged)

The sequence is **always-shippable**: each step compiles and leaves the suite usable. The content-validation steps are gated by a golden harness so the interim and future gates provably agree.

**Phase 0 - Characterize before you refactor (do FIRST):**
- Write GdUnit4 characterization tests pinning the current behavior of the pure-static `Validate` (LLMService.cs:257) and `ValidateScenario` (:500) - they are Godot-free and network-free, so trivially unit-testable. This is the golden baseline that proves the later "single shared entry point" refactor is behavior-preserving.

**Phase 1 - Provider abstraction (M2/M4, zero D3/D2 dependency, ships independently):**
1. Define `ILLMProvider` + `NormalizedRequest`/`NormalizedResult` (D6-1).
2. `AnthropicProvider` - verbatim lift of `TryClaudeAsync` (:164-207); update default model set to the Opus 4.8 / Sonnet 4.6 / Haiku 4.5 trio (Sonnet 4.6 stays the mid default, preserving :62).
3. `OllamaProvider` - migrate to `/api/chat` role messages (stop the lossy system+user concat at :219).
4. `OpenRouterProvider` - **new** (sequence LAST; gate behind the v1-or-fast-follow call).
5. Collapse the duplicated fallback chains (:118-131/:455-466) into the orchestrator; selected provider authoritative (D6-5, as a discrete commit, toggle default ON for now).
6. Smoke-test against the Snapshot.md "With API key" path and the no-key Ollama path.

**Phase 2 - Key migration + settings/UI (M2/M4, parallel with Phase 1):**
7. `ISecretStore` + `PlaintextSecretStore` writing to a gitignored `user://` file (D6-3); add the tested exclusion invariant (D6-4) - key absent from settings.json, all res:// writes, and main.tscn.
8. `SettingsData` fields + `AddDropdownRow`/`AddSecretRow` + AI section + Test-connection + four FR-34 states (D6-8); host-pinning + loopback validation + response-size cap (D6-10).
9. **Rip out** the `[Export] AnthropicApiKey` wiring (MainScene.cs:206/:1858/:1917); re-source from `SettingsManager`. This unlocks the genuinely-unconfigured `ProviderConfig` state that makes two of the four FR-34 states reachable.

**Phase 3 - Content ingest pipeline (gated on D3; PREFER after D3 lands):**
10. Replace the local `PropertyNameCaseInsensitive` options (:265/:508) with the D3 canonical `JsonSerializerOptions` + source-gen context.
11. Add the **quantization** stage: deserialize -> strip markdown -> `Fixed.FromFloat` canonicalize, with finiteness/magnitude clamps on every scalar reaching `Fixed.FromFloat` (StartOre/Supply/Rate/Amount/TimerSeconds/CooldownSeconds), BEFORE any hash (D6-7).
12. Route both generators through the single shared validator entry point; redirect that one call to `ContentLoader.Validate(model)` when D3 ships (the "one-line" part - but note the deserialize TARGET also swaps when D2's `NodeBase` lands; isolate prompt-schema + `deserialize<T>` behind a single "generation contract" to bound that).
13. **Golden gate:** a corpus of generated JSON run through BOTH the interim validator and (when available) `ContentLoader.Validate(model)`, asserting identical accept/reject AND identical canonical-model hash. Block the redirect on green.

**Phase 4 - MP hash reconciliation (binding, D6-9; co-owned with D3):**
14. Retire `ComputeFileHash` for MP agreement; the lobby Ready handshake exchanges the canonical Fixed-quantized model hash. The in-memory `_pendingGeneratedScenario` path computes and exchanges THAT hash; MP refuses to start if `ScenarioPath` and the live scenario diverge (fail-closed).

**Deferred fast-follows (post-1.0, behind existing seams):** DPAPI(Windows)/libsecret(Linux)/Keychain(mac) `ISecretStore` impls; SSE streaming; D2 prompt-schema + deserialize-target regeneration against `NodeBase`.

---

## 6. Prerequisites surfaced

1. **(Binding, Determinism)** The cross-peer agreement hash must move off `ScenarioSerializer.ComputeFileHash` (raw bytes, ScenarioSerializer.cs:59-80; computed at MainScene.cs:303-304) onto the canonical Fixed-quantized model hash. The in-memory `_pendingGeneratedScenario` (MainScene.cs:137,:466-469,:1959) currently leaves the stale on-disk hash in place - **verified**.
2. **(Binding, Determinism)** A float->Fixed quantization stage (`Fixed.FromFloat`, FixedPoint.cs:27 - truncating, no overflow/NaN/Inf guard - **verified**) must exist before any peer-comparable hash, and the interim validator must clamp all scalars reaching it.
3. **(Binding, D3)** The model-level gate must sit at the `ApplyScenario` boundary to cover the `ai_generated.json` saved-file round-trip (MapGeneratorPanel.cs:246 -> `ScenarioSerializer.LoadFromFile`, zero validation). A src/AI-local gate does not cover the durable artifact on reload.
4. **(Binding, D2 - originally missed)** The AI prompt schema (LLMService.cs:334-408, :600-664) and deserialize target (:264/:507) are bound to legacy `TriggerDefinition`/`ScenarioData` and must regenerate against D2's `NodeBase` graph IR. D6-7 is two swap points.
5. **(Floor, FR-29)** Confirm `user://settings.json` and the new key file are outside version control (Godot `user://` resolves outside the repo by default - **verify .gitignore, don't assume**) and excluded from all res:// writes / ContentPackager output. Remove the `[Export]` path - note **main.tscn currently has NO key line** (verified: the leak is a latent footgun, not a committed defect; justify the migration on FR-29's explicit text instead).
6. **(Confirm)** Whether AutoSave ever stages/commits `main.tscn` - this sets how urgent the `[Export]` rip-out is (dev types key -> scene dirtied -> AutoSave commits).
7. **(Confirm)** Whether settings.json is ever exported/synced - the grounding suggests no (FR-7 persistence is per-player-profile manifest data, not settings; no ContentPackager-of-settings path), which makes the separate-secrets-file precautionary-but-cheap rather than load-bearing.

---

## 7. Hand-offs

- **To D3 (schema & loader):** D6 is a CONSUMER of `ContentLoader.Validate(model)`, the canonical `JsonSerializerOptions`/source-gen context, and the canonical-model FNV-64-over-Fixed.Raw hash. The AI path is one of D3's "ALL paths (file/AI-gen/fallback/editor/replay)." **New co-dependency:** the MP-agreement-hash reconciliation (Phase 4) is the seam where D6 exposes a pre-existing D3-shaped hole; confirm ownership.
- **To D2 (graph IR):** when `NodeBase` lands, the AI prompt schema + deserialize target regenerate. Surface this as an explicit D2 hand-off, not only D3.
- **To M4 implementation:** build order is Phase 1->2 (independent) then Phase 3->4 (gated on D3). OpenRouter sequences last.
- **To SectionH / FR-31/32:** the scenario-type parameterization of the RTS clamps (faction 0|1 :275-282, >=2 slots :517, building enums :529-530, ore spacing 15u :574, <=6 combat :584 - wired to `ServerTransport.MAX_PLAYERS=2`/`FACTION_COUNT=5`) is a SEPARATE deliverable that sits ON TOP of the provider abstraction and is a PARAMETER on the D3 gate - do not couple it into the provider adapters.
- **To ContentPackager owner:** confirm the key never enters packaged/exported/synced artifacts (the D6-4 tested invariant).

---

## 8. Residual risks / watch-items

1. **MP desync on AI maps (was hidden as "zero determinism impact").** Until Phase 4 lands, an AI-generated/in-memory scenario carries the stale `ComputeFileHash` of the wrong file - the lobby check passes and the sim desyncs at tick 0. **This is the single most important watch-item and the reason the recommendation was revised.**
2. **Interim non-canonical gate window.** If you ship Phase 3 before D3, generated content is gated by the ad-hoc `Validate`/`ValidateScenario` with local non-canonical options - the golden equivalence harness limits but does not eliminate accept/reject drift. Preferred mitigation: sequence after D3.
3. **`ai_generated.json` round-trip bypass.** The saved artifact re-enters via `ScenarioSerializer.LoadFromFile` (zero validation) on later launches - a *complete* bypass for the persisted file, not just drift, until the ApplyScenario gate exists.
4. **Numeric overflow on LLM floats.** `Fixed.FromFloat` has no checked/clamp; `supply=1e39` -> garbage platform-sensitive `Fixed.Raw`. The interim clamps (prereq #2) close this; without them the highest-entropy input is the least-guarded.
5. **Key exfiltration via base URL.** A free-text host + Test-connection = one-click key leak via a forged/synced settings.json. Closed by D6-10 host-pinning + loopback-only-Ollama.
6. **Cross-platform key-at-rest gap.** The plaintext floor is encryption-absent until DPAPI/libsecret land - mitigated only by never-committed/never-synced. Accepted as deferrable for a Windows-primary solo dev.
7. **Blocking-v1 vs 30s timeout on the flagship.** If Opus 4.8 7-pass map-gen exceeds 30s (LLMService.cs:69), it surfaces as FR-34 "request failed" on the default premium model. Measure + raise before locking (D6-9); cheaper than streaming.
8. **OpenRouter free-tier JSON validity.** Weak free models reject more often at the validator -> more FR-34 "validation failed." UX/perception risk, not correctness.
9. **Curated model-list drift.** Free-text override mitigates; refresh the curated trio when newer Claude models ship.

---

## 9. Adversarial review note (4 lenses)

The review materially changed the recommendation. Summary of what landed and what I rejected:

- **Determinism & Lockstep (verdict: flawed -> folded as binding).** Two CRITICAL findings, both verified in code: (a) the cross-peer hash is raw-byte `ComputeFileHash` over a stale `ScenarioPath` while AI maps live in `_pendingGeneratedScenario` and never touch disk -> silent desync; (b) no quantization stage and an unspecified hash domain, with `Fixed.FromFloat` truncating and unguarded. **Folded** as prerequisites #1-2, D6-7, and the NEW D6-9. The original "determinismImpact: Zero" framing was wrong and is corrected throughout. The minor commit-ordering/immutability invariant is folded into D6-9.

- **Static-validation & anti-tamper (verdict: sound-with-changes).** CRITICAL: the model-level gate validates SHAPE, not peer AGREEMENT, and the in-memory path bypasses the only integrity check - **folded** (prereq #1, D6-9). MAJOR: the `ai_generated.json` round-trip re-enters through the unvalidated file path -> **folded** (prereq #3, risk #3); base-URL key-exfiltration + unbounded response read -> **folded** as the NEW D6-10. MINOR: untrusted new SettingsData fields -> folded into D6-10.

- **Brownfield-fit & D1/D2/D3-coherence (verdict: sound-with-changes).** MAJOR: D6-7 is **two** swap points (deserialize target + validator) because D2 replaces the model - **folded** (prereq #4, D6-7 reframed honestly). MAJOR: no golden-checksum gate exists because the canonical hash is itself a D3 deliverable -> **folded** (Phase 0 characterization + Phase 3 golden harness, and the recommendation now PREFERS sequencing after D3). MINOR: the FR-34 baseline was mischaracterized (the `_llm==null` branch is dead; the real signal is the completion-error string) -> **folded** into D6-8.

- **Scope & solo-dev cost (verdict: sound-with-changes).** MAJOR and accepted: the "committable [Export] leak" is **not** an existing defect - main.tscn has no key line (verified) - so the justification is reframed onto FR-29's explicit text, not a phantom leak. MAJOR and accepted: DPAPI-now is gold-plating a non-load-bearing surface -> **the storage recommendation was reversed** to plaintext-floor-behind-the-seam (D6-3). MAJOR and accepted: D6 must split by milestone (provider/key half independent; validation half gated on D3) -> reflected in the phased sequence. MINOR: bundle D6-5 as a discrete commit with default-ON-until-D6-8 -> folded.

**Rejected / down-weighted:** none of the findings were rejected outright; the only down-weighting is that the `[Export]` rip-out remains in scope (it's FR-29's named requirement) even though the "leak" framing was wrong - so the work stays, the *justification* changes. The OpenRouter-deferrability and AutoSave-touches-.tscn questions are surfaced as confirm-items rather than assumed.
