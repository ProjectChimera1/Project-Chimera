# Epic 8 Context: AI-Assisted Creation

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Add an AI collaborator across every editor in the creation suite — provider configuration, generation of triggers, maps, units/abilities/heroes/factions, and balance analysis — while guaranteeing the entire manual authoring suite stays fully usable when AI is unavailable. This epic replaces today's brittle brownfield AI (two hardcoded generate methods with an implicit Claude→Ollama fallback, plaintext API keys in Inspector-exported fields) with a secret-store-backed, provider-agnostic stack. It matters because AI-assisted authoring is the platform's differentiator, but it must never compromise determinism or lock the creator out of manual control: every AI output is an editable draft that passes the same validation gate as hand-authored data.

## Stories

- Story 8.1: Provider config — secret store + provider/model/baseUrl in versioned settings
- Story 8.2: Godot-free provider abstraction (Anthropic/Ollama/OpenRouter) + four-state availability UI + test-connection
- Story 8.3: AI generation on the provider stack — triggers (+ new DSL constructs) + maps
- Story 8.4: AI entity drafts as editable data — unit/ability/hero/faction
- Story 8.5: AI balance analysis of a faction/scenario with editable suggestions

## Requirements & Constraints

- Creators select and configure an LLM provider (Anthropic/Claude, local Ollama, OpenRouter) by supplying a key; keys are never hardcoded, committed, or shipped in a build. This includes migrating key storage off the existing plaintext Inspector-exported fields into the persisted settings/secret system.
- Trigger generation from natural language already exists — verify it, and extend the prompt schema + validator to cover DSL constructs added by earlier epics that the current prompt omits. An unknown construct must be rejected with a located error.
- Map generation already exists — verify it, and relax/parameterize the validator's RTS-only hard clamps (≤6 combat units/faction, exactly-2 player slots, forced faction-JSON paths) so non-RTS scenario types are not wrongly rejected. RTS presets keep today's defaults to avoid regression. Positions/spacing/bounds checks always run; maps load only after passing validation.
- Creators can generate unit, ability, hero, and faction drafts (stats + name + lore) as fully editable data matching the existing definition schemas. Drafts are never locked — always reopenable and editable.
- Creators can request AI balance analysis of a faction/scenario and receive actionable, human-readable suggestions tied to specific fields, as editable data. Nothing is auto-applied; the creator reviews/edits/discards each suggestion, and applied changes go through the validation gate. If a response fails to parse into structured suggestions, surface the failed-validation state and leave the underlying data unchanged.
- Graceful degradation is mandatory: with no provider/key or an unreachable/failed-validation provider, AI affordances disable and explain themselves while every editor remains fully usable manually.
- AI calls never block the deterministic sim; generation happens in the editor/authoring layer only.

## Technical Decisions

- **Authoring-layer only, zero sim coupling.** No AI/LLM code runs in the sim tick. Any float in generated output is quantized to Fixed by the SAME validation gate before persistence or any canonical hash, so two runs hash identically. This quantize-before-hash contract is established once (unit/ability draft framework) and reused verbatim by hero/faction drafts and balance-apply.
- **Secret store seam.** A net-new pure-C# (Godot-free) `ISecretStore` interface with a file-backed impl over a gitignored `user://secrets/*.key`. On a fresh project it returns empty and writes nothing until a key is explicitly saved. Rip out the plaintext `[Export] AnthropicApiKey` and `ModIoApiKey` fields on MainScene; migrate any existing value on first run. `LLMService` and `ModIoService` read keys through this seam. Add `user://secrets/` and `*.key` to `.gitignore`, plus a `SecretExclusionTest` that fails loudly if any key string appears in the exported build/PCK.
- **Versioned settings.** Add a schema-version field plus provider/model/baseUrl to `SettingsData` (currently has none) and migrate older save files forward with safe defaults. Curated, data-driven provider list (Anthropic, Ollama, OpenRouter) with per-provider curated model lists PLUS a free-text model override. Default model `claude-sonnet-4-6`. The API key stays in the secret store, never in settings.json.
- **Godot-free provider abstraction.** A hand-rolled `ILLMProvider.GenerateAsync(NormalizedRequest) → NormalizedResult` over three adapters (Anthropic `/v1/messages`, Ollama `/api/chat`, OpenRouter `/chat/completions`). No vendor SDK — only `System.Net.Http` — so the build stays AOT-clean. Blocking v1. The selected provider is authoritative: it must NOT silently fall back to another provider (this replaces today's implicit Claude→Ollama fallback). Cloud hosts are on a pinned allowlist; buffered response bytes are capped.
- **Re-point existing services.** `LLMService.GenerateTriggerAsync` and `GenerateScenarioAsync` move off their hardcoded provider calls onto `ILLMProvider`. Verify the existing multi-pass trigger and scenario validators still gate output and the review/edit-before-apply flow still works.
- **Composition over inheritance for drafts.** Generated entity drafts express behavior via archetype + ability composition, never RTS-only assumptions or bespoke subclasses. Each emits JSON matching the existing Core/Definitions data classes and lands as editable data into the existing data/file flow (or an entity editor host if one exists). If an editor host is missing, drafts still save as editable JSON the manual editor already consumes.
- **Known gap — "scenario type" is undefined.** Story 8.5 (map clamps) references scenario-type parameters, but no story defines a ScenarioType schema/registry. Do NOT source the relaxed limits from the untrusted scenario file itself (circular validation weakens the gate). Flag this for a decision if implementing the clamp relaxation.

## UX & Interaction Patterns

- **Four-state availability UI (required on every AI panel):** (1) no provider configured, (2) provider set but no key, (3) unreachable host, (4) provider returned but response failed validation — each a distinct, clear message. A Test-connection action performs a minimal round-trip and reports which state applies. In every state the editor remains fully usable manually.
- **"Transmuting…" spinner** shows during any AI call (generation or analysis).
- **Voice/microcopy:** address the creator as "Commander"; confident, terse, mechanical. Short concrete button verbs (Generate, Deploy, Publish). Ownership stated plainly: "you own what you make." All AI output is editable and reopenable, never locked.

## Cross-Story Dependencies

- 8.1 (secrets + versioned settings) lands first — it is consumed by everything.
- 8.2 (provider abstraction + four-state UI) depends on 8.1 and gates all generation features (8.3–8.5), which route through it.
- 8.3 (triggers + maps) is verify/extend of existing brownfield code; depends on 8.2.
- 8.4 (unit/ability/hero/faction drafts) depends on 8.2 and establishes the quantize-before-hash draft contract.
- 8.5 (balance analysis) is fully new and depends on 8.2 and 8.4 (reuses the definition schemas and the draft/apply validation contract).
- Assumes earlier epics delivered the editor scaffolds for units/abilities/heroes/factions, the validation gate, and the trigger DSL. Epic 9 (multiplayer at scale) is sequenced after Epic 8.
